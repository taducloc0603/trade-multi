#include "engine.h"
#include <algorithm>
#include <cwctype>
#include <utility>

namespace MetaTraderEngine {

std::string WstrToUtf8(const std::wstring& ws)
{
    if (ws.empty()) return {};
    int sz = WideCharToMultiByte(CP_UTF8, 0, ws.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string s(sz - 1, 0);
    WideCharToMultiByte(CP_UTF8, 0, ws.c_str(), -1, s.data(), sz, nullptr, nullptr);
    return s;
}

static BOOL CALLBACK _EnumWindowsProc(HWND hwnd, LPARAM lp)
{
    if (!IsWindowVisible(hwnd)) return TRUE;
    auto* vec = reinterpret_cast<std::vector<WindowInfo>*>(lp);
    wchar_t title[256] = {}, cls[128] = {};
    GetWindowTextW(hwnd, title, 256);
    GetClassNameW(hwnd, cls, 128);
    vec->push_back({ hwnd, title, cls });
    return TRUE;
}

static BOOL CALLBACK _EnumChildProc(HWND hwnd, LPARAM lp)
{
    auto* vec = reinterpret_cast<std::vector<WindowInfo>*>(lp);
    wchar_t title[256] = {}, cls[128] = {};
    GetWindowTextW(hwnd, title, 256);
    GetClassNameW(hwnd, cls, 128);
    vec->push_back({ hwnd, title, cls });
    return TRUE;
}

std::vector<WindowInfo> EnumTopLevelWindows()
{
    std::vector<WindowInfo> v;
    EnumWindows(_EnumWindowsProc, reinterpret_cast<LPARAM>(&v));
    return v;
}

std::vector<WindowInfo> EnumChildWindowList(HWND parent)
{
    std::vector<WindowInfo> v;
    EnumChildWindows(parent, _EnumChildProc, reinterpret_cast<LPARAM>(&v));
    return v;
}

HWND FindListView(HWND hwnd)
{
    wchar_t cls[64] = {};
    GetClassNameW(hwnd, cls, 64);
    if (wcscmp(cls, L"SysListView32") == 0) return hwnd;
    HWND child = GetWindow(hwnd, GW_CHILD);
    while (child) {
        GetClassNameW(child, cls, 64);
        if (wcscmp(cls, L"SysListView32") == 0) return child;
        child = GetWindow(child, GW_HWNDNEXT);
    }
    return NULL;
}

int GetRowCount(HWND hLV)
{
    if (!hLV || !IsWindow(hLV)) return 0;
    DWORD_PTR result = 0;
    SendMessageTimeout(hLV, LVM_GETITEMCOUNT, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 200, &result);
    return static_cast<int>(result);
}

void Context::Attach(HWND lv)
{
    Release();
    hLV  = lv;
    hMT5 = lv ? GetAncestor(lv, GA_ROOT) : NULL;
    pid  = 0;
    cachedRows = 0;
    if (lv) {
        GetWindowThreadProcessId(lv, &pid);
        hProc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, FALSE, pid);
    }
}

void Context::Release()
{
    if (hProc) { CloseHandle(hProc); hProc = NULL; }
    hLV = hMT5 = NULL;
    pid = 0;
    cachedRows = 0;
}

Context* CreateContext(HWND hLV)
{
    auto* ctx = new Context();
    ctx->Attach(hLV);
    return ctx;
}

void DestroyContext(Context* ctx)
{
    if (ctx) ctx->Release();
}

int UpdateRowCount(Context* ctx)
{
    if (!ctx || !ctx->hLV || !IsWindow(ctx->hLV)) return 0;
    DWORD_PTR result = 0;
    if (SendMessageTimeout(ctx->hLV, LVM_GETITEMCOUNT, 0, 0,
                           SMTO_ABORTIFHUNG | SMTO_NOTIMEOUTIFNOTHUNG, 200, &result))
        ctx->cachedRows = static_cast<int>(result);
    return static_cast<int>(ctx->cachedRows);
}

static bool SelectOnlyRow(Context* ctx, int rowIdx)
{
    if (!ctx || !ctx->hLV || !IsWindow(ctx->hLV) || !ctx->hProc || rowIdx < 0)
        return false;

    LVITEM local = {};
    local.stateMask = LVIS_SELECTED | LVIS_FOCUSED;

    LVITEM* remote = static_cast<LVITEM*>(
        VirtualAllocEx(ctx->hProc, nullptr, sizeof(LVITEM), MEM_COMMIT, PAGE_READWRITE));
    if (!remote) return false;

    bool success = false;
    do {
        // Clear any row that the user (or a previous close) left selected. The MT
        // close command acts on the selection, so retaining it can close multiple slots.
        local.state = 0;
        if (!WriteProcessMemory(ctx->hProc, remote, &local, sizeof(LVITEM), nullptr))
            break;

        DWORD_PTR clearResult = 0;
        if (!SendMessageTimeout(ctx->hLV, LVM_SETITEMSTATE, static_cast<WPARAM>(-1),
                                reinterpret_cast<LPARAM>(remote),
                                SMTO_ABORTIFHUNG | SMTO_BLOCK, 200, &clearResult))
            break;

        local.state = LVIS_SELECTED | LVIS_FOCUSED;
        if (!WriteProcessMemory(ctx->hProc, remote, &local, sizeof(LVITEM), nullptr))
            break;

        DWORD_PTR selectResult = 0;
        if (!SendMessageTimeout(ctx->hLV, LVM_SETITEMSTATE, static_cast<WPARAM>(rowIdx),
                                reinterpret_cast<LPARAM>(remote),
                                SMTO_ABORTIFHUNG | SMTO_BLOCK, 200, &selectResult))
            break;

        success = true;
    } while (false);

    VirtualFreeEx(ctx->hProc, remote, 0, MEM_RELEASE);
    return success;
}

bool ClosePositionMT5(Context* ctx, int rowIdx)
{
    if (!ctx || !ctx->hLV || !IsWindow(ctx->hLV) || !ctx->hProc || !ctx->hMT5)
        return false;

    if (!SelectOnlyRow(ctx, rowIdx)) return false;

    PostMessage(ctx->hMT5, WM_COMMAND, MAKEWPARAM(33033, 0), 0);
    return true;
}

bool ClosePositionMT4(Context* ctx, int rowIdx)
{
    if (!ctx || !ctx->hLV || !IsWindow(ctx->hLV) || !ctx->hProc || !ctx->hMT5)
        return false;

    if (!SelectOnlyRow(ctx, rowIdx)) return false;

    PostMessage(ctx->hMT5, WM_COMMAND, MAKEWPARAM(35451, 0), 0);
    return true;
}

namespace {

bool ReadListViewCell(HWND listView, HANDLE process, int row, int column,
                      std::wstring& value)
{
    value.clear();
    constexpr int capacity = 256;
    auto* remoteText = static_cast<wchar_t*>(VirtualAllocEx(
        process, nullptr, capacity * sizeof(wchar_t), MEM_COMMIT | MEM_RESERVE,
        PAGE_READWRITE));
    auto* remoteItem = static_cast<LVITEMW*>(VirtualAllocEx(
        process, nullptr, sizeof(LVITEMW), MEM_COMMIT | MEM_RESERVE,
        PAGE_READWRITE));
    if (!remoteText || !remoteItem) {
        if (remoteText) VirtualFreeEx(process, remoteText, 0, MEM_RELEASE);
        if (remoteItem) VirtualFreeEx(process, remoteItem, 0, MEM_RELEASE);
        return false;
    }

    LVITEMW item = {};
    item.mask = LVIF_TEXT;
    item.iSubItem = column;
    item.pszText = remoteText;
    item.cchTextMax = capacity;
    bool ok = WriteProcessMemory(process, remoteItem, &item, sizeof(item), nullptr) != FALSE;
    DWORD_PTR copied = 0;
    if (ok) {
        ok = SendMessageTimeoutW(listView, LVM_GETITEMTEXTW,
                                 static_cast<WPARAM>(row),
                                 reinterpret_cast<LPARAM>(remoteItem),
                                 SMTO_ABORTIFHUNG | SMTO_BLOCK, 200,
                                 &copied) != FALSE;
    }
    wchar_t localText[capacity] = {};
    if (ok) {
        ok = ReadProcessMemory(process, remoteText, localText,
                               sizeof(localText), nullptr) != FALSE;
    }
    VirtualFreeEx(process, remoteText, 0, MEM_RELEASE);
    VirtualFreeEx(process, remoteItem, 0, MEM_RELEASE);
    if (!ok) return false;
    value.assign(localText);
    return true;
}

bool CellMatchesTicket(const std::wstring& value, uint64_t ticket)
{
    auto first = std::find_if(value.begin(), value.end(),
        [](wchar_t ch) { return !std::iswspace(ch) && ch != L'#'; });
    auto last = std::find_if(value.rbegin(), value.rend(),
        [](wchar_t ch) { return !std::iswspace(ch); }).base();
    if (first >= last) return false;
    return std::wstring(first, last) == std::to_wstring(ticket);
}

} // namespace

Context* CreateContextForTicket(HWND descendant, uint64_t ticket, int& rowIdx)
{
    rowIdx = -1;
    if (!descendant || !IsWindow(descendant) || ticket == 0) return nullptr;
    HWND root = GetAncestor(descendant, GA_ROOT);
    if (!root) return nullptr;

    auto children = EnumChildWindowList(root);
    for (const auto& child : children) {
        if (child.className != L"SysListView32") continue;
        int rows = GetRowCount(child.hwnd);
        if (rows <= 0) continue;
        DWORD pid = 0;
        GetWindowThreadProcessId(child.hwnd, &pid);
        HANDLE process = OpenProcess(
            PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE,
            FALSE, pid);
        if (!process) continue;
        bool found = false;
        for (int row = 0; row < rows && !found; ++row) {
            for (int column = 0; column < 16; ++column) {
                std::wstring value;
                if (ReadListViewCell(child.hwnd, process, row, column, value) &&
                    CellMatchesTicket(value, ticket)) {
                    rowIdx = row;
                    found = true;
                    break;
                }
            }
        }
        CloseHandle(process);
        if (found) return CreateContext(child.hwnd);
    }
    return nullptr;
}

void PostClick(HWND hwnd, int cx, int cy)
{
    LPARAM lp = MAKELPARAM(cx, cy);
    PostMessage(hwnd, WM_MOUSEMOVE,   0,          lp);
    PostMessage(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, lp);
    PostMessage(hwnd, WM_LBUTTONUP,   0,          lp);
}

} // namespace MetaTraderEngine

HWND HwndFromPoint(int x, int y)
{
    POINT pt = { x, y };
    HWND hwnd = WindowFromPoint(pt);
    return hwnd;
}

std::string GetWindowClass(HWND hwnd)
{
    wchar_t cls[128] = {};
    GetClassNameW(hwnd, cls, 128);
    return MetaTraderEngine::WstrToUtf8(cls);
}
