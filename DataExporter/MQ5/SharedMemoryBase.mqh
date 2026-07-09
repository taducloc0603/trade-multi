#property strict

#include "Configs.mqh"

#import "kernel32.dll"
   ulong CreateFileMappingW(ulong hFile, ulong lpAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);
   ulong MapViewOfFile(ulong hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, ulong dwNumberOfBytesToMap);
   bool UnmapViewOfFile(ulong lpBaseAddress);
   bool CloseHandle(ulong hObject);
   void RtlMoveMemory(ulong dest, uchar &src[], uint size);
#import

#define HEADER_SIZE 16 // count(4) + timestamp(8) + padding(4)

class CSharedMemoryBase {
protected:
   ulong m_hMap;
   ulong m_pMem;
   uint m_size;
   string m_name;
   
public:
   CSharedMemoryBase(string name, uint size) {
      m_hMap = 0;
      m_pMem = 0;
      m_size = size;
      m_name = name;
   }
   
   ~CSharedMemoryBase() {
      Close();
   }
   
   bool Init() {
      m_hMap = CreateFileMappingW(INVALID_HANDLE, 0, PAGE_READWRITE, 0, m_size, m_name);
      if(!m_hMap) {
         return false;
      }
      
      m_pMem = MapViewOfFile(m_hMap, FILE_MAP_ALL_ACCESS, 0, 0, m_size);
      if(!m_pMem) {
         CloseHandle(m_hMap);
         m_hMap = 0;
         return false;
      }
      return true;
   }
   
   void Close() {
      if(m_pMem) UnmapViewOfFile(m_pMem);
      if(m_hMap) CloseHandle(m_hMap);
      m_pMem = 0;
      m_hMap = 0;
   }
   
   bool IsValid() {
      return m_pMem != 0;
   }
   
protected:
   void WriteJSON(string json) {
      if(!IsValid()) return;

      uchar buf[];
      int size = StringToCharArray(json, buf, 0, WHOLE_ARRAY, CP_UTF8);
      RtlMoveMemory(m_pMem, buf, size);
   }

   // Ghi buf vào shared memory theo thứ tự: body trước, header sau.
   // Reader skip frame khi timestamp ở header chưa đổi, nhờ đó body luôn
   // hoàn chỉnh trước khi reader thấy timestamp mới — giảm torn read mà
   // không thay đổi layout binary (header 16B vẫn ở offset 0).
   void CommitWrite(uchar &buf[]) {
      if(!IsValid()) return;
      int total = ArraySize(buf);
      if(total <= HEADER_SIZE) {
         RtlMoveMemory(m_pMem, buf, total);
         return;
      }

      int bodySize = total - HEADER_SIZE;
      uchar body[];
      ArrayResize(body, bodySize);
      ArrayCopy(body, buf, 0, HEADER_SIZE, bodySize);

      uchar header[];
      ArrayResize(header, HEADER_SIZE);
      ArrayCopy(header, buf, 0, 0, HEADER_SIZE);

      // Body trước (reader vẫn thấy timestamp cũ ⇒ skip).
      RtlMoveMemory(m_pMem + HEADER_SIZE, body, bodySize);
      // Header sau (commit — reader thấy timestamp mới ⇒ body đã hoàn chỉnh).
      RtlMoveMemory(m_pMem, header, HEADER_SIZE);
   }
};