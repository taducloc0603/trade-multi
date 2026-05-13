#pragma once

#include <stdint.h>

#if defined(_WIN32)
#define MT_API extern "C" __declspec(dllexport)
#else
#define MT_API extern "C"
#endif

MT_API int      mt_is_valid_window(uint64_t hwnd);
MT_API uint64_t mt_find_list_view(uint64_t parentHwnd);

MT_API void* mt_create_context(uint64_t listViewHwnd);
MT_API void* mt_create_context_from_parent(uint64_t parentHwnd);
MT_API int   mt_update_row_count(void* ctx);
MT_API int   mt_close_position_mt5(void* ctx, int rowIdx);
MT_API int   mt_close_position_mt4(void* ctx, int rowIdx);
MT_API void  mt_destroy_context(void* ctx);

MT_API int mt_click_buy(uint64_t chartHwnd);
MT_API int mt_click_sell(uint64_t chartHwnd);
