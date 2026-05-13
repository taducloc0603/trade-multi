#include "engine.h"
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

bool ClosePositionMT5(Context* ctx, int rowIdx)
{
    if (!ctx || !ctx->hLV || !IsWindow(ctx->hLV) || !ctx->hProc || !ctx->hMT5)
        return false;

    LVITEM local    = {};
    local.stateMask = LVIS_SELECTED | LVIS_FOCUSED;
    local.state     = LVIS_SELECTED | LVIS_FOCUSED;

    LVITEM* remote = static_cast<LVITEM*>(
        VirtualAllocEx(ctx->hProc, nullptr, sizeof(LVITEM), MEM_COMMIT, PAGE_READWRITE));
    if (!remote) return false;

    WriteProcessMemory(ctx->hProc, remote, &local, sizeof(LVITEM), nullptr);
    PostMessage(ctx->hLV, LVM_SETITEMSTATE, static_cast<WPARAM>(rowIdx), reinterpret_cast<LPARAM>(remote));

    using Pair = std::pair<HANDLE, LPVOID>;
    HANDLE hProcCopy = ctx->hProc;
    HANDLE hThread = CreateThread(nullptr, 0,
        [](LPVOID arg) -> DWORD {
            auto* p = reinterpret_cast<Pair*>(arg);
            Sleep(50);
            VirtualFreeEx(p->first, p->second, 0, MEM_RELEASE);
            delete p;
            return 0;
        },
        new Pair(hProcCopy, remote), 0, nullptr);
    if (hThread) CloseHandle(hThread);

    PostMessage(ctx->hMT5, WM_COMMAND, MAKEWPARAM(33033, 0), 0);
    return true;
}

bool ClosePositionMT4(Context* ctx, int rowIdx)
{
    if (!ctx || !ctx->hLV || !IsWindow(ctx->hLV) || !ctx->hProc || !ctx->hMT5)
        return false;

    LVITEM local    = {};
    local.stateMask = LVIS_SELECTED | LVIS_FOCUSED;
    local.state     = LVIS_SELECTED | LVIS_FOCUSED;

    LVITEM* remote = static_cast<LVITEM*>(
        VirtualAllocEx(ctx->hProc, nullptr, sizeof(LVITEM), MEM_COMMIT, PAGE_READWRITE));
    if (!remote) return false;

    WriteProcessMemory(ctx->hProc, remote, &local, sizeof(LVITEM), nullptr);
    PostMessage(ctx->hLV, LVM_SETITEMSTATE, static_cast<WPARAM>(rowIdx), reinterpret_cast<LPARAM>(remote));

    using Pair = std::pair<HANDLE, LPVOID>;
    HANDLE hProcCopy = ctx->hProc;
    HANDLE hThread = CreateThread(nullptr, 0,
        [](LPVOID arg) -> DWORD {
            auto* p = reinterpret_cast<Pair*>(arg);
            Sleep(50);
            VirtualFreeEx(p->first, p->second, 0, MEM_RELEASE);
            delete p;
            return 0;
        },
        new Pair(hProcCopy, remote), 0, nullptr);
    if (hThread) CloseHandle(hThread);

    PostMessage(ctx->hMT5, WM_COMMAND, MAKEWPARAM(35451, 0), 0);
    return true;
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
