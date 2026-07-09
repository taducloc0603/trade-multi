#define FILE_MAP_ALL_ACCESS 0xF001F
#define FILE_MAP_READ       0x0004
#define FILE_MAP_WRITE      0x0002
#define PAGE_READWRITE      0x04
#define INVALID_HANDLE_VALUE_INT -1

#define MAX_HISTORY_DEALS 100
#define HISTORY_PERIOD_SECONDS 86400  // 24 hours

// Size hằng số (memory size, record size, symbol size, header size) được
// định nghĩa trong từng file Memory class (TickMemory4 / TradesMemory4 /
// HistoryMemory4 / SharedMemoryBase4) để tránh redefinition mismatch.
