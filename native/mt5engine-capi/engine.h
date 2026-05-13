#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <commctrl.h>
#include <string>
#include <vector>
#include <atomic>

#pragma comment(lib, "comctl32.lib")

namespace MetaTraderEngine {

struct WindowInfo {
    HWND         hwnd = NULL;
    std::wstring title;
    std::wstring className;
};

struct Context {
    HWND             hLV   = NULL;
    HWND             hMT5  = NULL;
    HANDLE           hProc = NULL;
    DWORD            pid   = 0;
    std::atomic<int> cachedRows{0};

    void Attach(HWND lv);
    void Release();

    Context() = default;
    ~Context() { Release(); }
    Context(const Context&)            = delete;
    Context& operator=(const Context&) = delete;
};

std::vector<WindowInfo> EnumTopLevelWindows();
std::vector<WindowInfo> EnumChildWindowList(HWND parent);

HWND FindListView(HWND hwnd);
int  GetRowCount(HWND hLV);

Context* CreateContext(HWND hLV);
void     DestroyContext(Context* ctx);

int  UpdateRowCount(Context* ctx);
bool ClosePositionMT5(Context* ctx, int rowIdx);
bool ClosePositionMT4(Context* ctx, int rowIdx);
void PostClick(HWND hwnd, int cx, int cy);

std::string WstrToUtf8(const std::wstring& ws);

} // namespace MetaTraderEngine

HWND HwndFromPoint(int x, int y);
std::string GetWindowClass(HWND hwnd);
