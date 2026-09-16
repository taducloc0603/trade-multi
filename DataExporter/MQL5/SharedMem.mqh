//+------------------------------------------------------------------+
//|                                                    SharedMem.mqh |
//|                                                    Dang Hai Long |
//|                                          https://fb.com/longdhit |
//+------------------------------------------------------------------+
//| Shared-memory transport thay the TCP server (copy-trade).          |
//|                                                                    |
//| KIEN TRUC (v4)                                                     |
//|   Ca room dung DUNG MOT vung shared memory:                         |
//|       Local\CopyTradeRoom_<roomId>                                  |
//|   Ten vung CHI phu thuoc roomId. Hai terminal nhap cung roomId la   |
//|   thay nhau ngay - khong con canh moi ben tu tinh ra mot ten khac   |
//|   nhau roi khong bao gio gap.                                       |
//|                                                                     |
//|   Ben trong vung chia SHM_MAX_LANES lane doc lap. Moi writer chiem   |
//|   DUNG MOT lane va ghi danh uid vao REGISTRY, nen van la 1 writer /  |
//|   1 lane. Reader doc tat ca lane tru lane cua chinh no roi tron.     |
//|                                                                     |
//|   VI SAO phai chia lane thay vi dung chung mot ring? Vi tren x64,    |
//|   InterlockedCompareExchange / InterlockedExchangeAdd64 KHONG duoc   |
//|   kernel32 export (chung la compiler intrinsic, bien dich thanh      |
//|   'lock cmpxchg' inline - da kiem tra bang dumpbin: kernel32 chi     |
//|   export cac bien the SList). MQL vi vay KHONG THE #import atomic    |
//|   that su: khai bao van bien dich nhung se loi luc chay.            |
//|   Neu hai writer dung chung mot ring thi ca hai cung doc write_seq,  |
//|   cung ghi mot slot, va MOT TIN HIEU BIEN MAT ma seqlock khong phat  |
//|   hien duoc (ca hai deu ghi seqlock hop le). Chia lane loai bo hoan  |
//|   toan tinh huong do: khong can atomic, khong mat record, va mien    |
//|   nhiem voi viec EA chet giua duong (khong co khoa nao de bi bo roi).|
//|                                                                     |
//|   Vung = HEADER(64B) + REGISTRY(8 x 32B) + 8 lane x 256 slot x 1024B.|
//|   Ring ghi de: writer KHONG BAO GIO doc cursor cua reader, nen slave |
//|   treo hay chet khong the lam nghen master.                          |
//|                                                                     |
//|   Moi slot bao ve bang "seqlock": writer ghi seq_begin, ghi payload, |
//|   roi ghi seq_end. Reader doc seq_end -> payload -> seq_begin va chi |
//|   chap nhan khi hai gia tri khop. Record dang ghi (hoac writer chet  |
//|   giua duong) luon bi phat hien thay vi doc ra rac.                  |
//|                                                                     |
//| AN TOAN BO NHO (doc ky truoc khi sua)                               |
//|   1. RtlMoveMemory KHONG kiem tra bien. HUONG copy do CHINH CHU KY   |
//|      quyet dinh, khong phai ten ham:                                |
//|         RtlMoveMemory(uchar &dst[], <ptr> src, len)  = shm -> MQL    |
//|         RtlMoveMemory(<ptr> dst, uchar &src[], len)  = MQL -> shm    |
//|      Goi sai huong VAN BIEN DICH va VAN CHAY, nhung se ghi de vao    |
//|      heap cua terminal. Luon dung ShmRead*/ShmWrite* ben duoi.       |
//|   2. Luon ArrayResize truoc khi dung array lam dich. Array rong cho  |
//|      dia chi phan tu 0 khong hop le -> crash terminal.               |
//|   3. Chi dua vao TSO cua x86/x64 (store retire theo thu tu chuong    |
//|      trinh). Tren ARM/Wine thu tu ghi co the bi dao -> sai seqlock.  |
//|   4. Ham #import BI ham co san cua MQL che khuat. Thu tu uu tien la: |
//|      method cua class -> ham co san -> ham toan cuc -> ham #import.  |
//|      Vi vay PHAI viet kernel32::GetLastError() moi lay duoc ma loi   |
//|      Win32; viet GetLastError() se lay _LastError cua MQL.           |
//+------------------------------------------------------------------+
#property copyright "Dang Hai Long"
#property link      "https://fb.com/longdhit"

#ifndef __SHAREDMEM_MQH__
#define __SHAREDMEM_MQH__

//--- Win32 constants
#define SHM_INVALID_HANDLE_VALUE  (-1)
#define SHM_PAGE_READWRITE        0x00000004
#define SHM_FILE_MAP_ALL_ACCESS   0x000F001F
#define SHM_ERROR_ALREADY_EXISTS  183

//--- Layout v4 (giu dong bo voi TradeDesktop.Infrastructure/Mt5Bridge)
//
//    MOT vung duy nhat cho ca room:  Local\CopyTradeRoom_<roomId>
//    Ben trong chia thanh SHM_MAX_LANES lane doc lap. Moi writer chiem
//    DUNG MOT lane (ghi danh uid vao REGISTRY), nen van la 1 writer/lane
//    => khong can atomic, seqlock giu nguyen y nghia.
//
//    +--------------------------------------------------------------+
//    | HEADER 64B | REGISTRY 8 x 32B | lane 0 | lane 1 | ... lane 7 |
//    +--------------------------------------------------------------+
//                                    moi lane = 256 slot x 1024B
#define SHM_MAGIC          0x43505452   // 'CPTR'
#define SHM_VERSION        4
#define SHM_MAX_LANES      8            // so writer toi da trong 1 room
#define SHM_CAPACITY       256          // so slot MOI LANE, phai la luy thua cua 2
#define SHM_CAPACITY_MASK  (SHM_CAPACITY - 1)
#define SHM_SLOT_SIZE      1024
#define SHM_HEADER_SIZE    64

//--- REGISTRY: bang ghi danh writer. Moi entry 32 byte.
#define SHM_REG_ENTRY_SIZE 32
#define SHM_REG_OFF        SHM_HEADER_SIZE
#define SHM_REG_SIZE       (SHM_MAX_LANES * SHM_REG_ENTRY_SIZE)
//--- offset trong 1 entry
#define SHM_REG_OFF_CLAIMED  0    // uint  1 = lane da co chu
#define SHM_REG_OFF_UID      4    // uint  uid cua writer (dang so)
#define SHM_REG_OFF_HEARTBEAT 8   // ulong tick cua lan Send() gan nhat
#define SHM_REG_OFF_WRITESEQ 16   // ulong so record lane nay da ghi (HOT)
#define SHM_REG_OFF_GENERATION 24 // ulong instance generation cua writer

//--- vung lane bat dau sau REGISTRY
#define SHM_LANES_OFF      (SHM_REG_OFF + SHM_REG_SIZE)
#define SHM_LANE_SIZE      (SHM_CAPACITY * SHM_SLOT_SIZE)
#define SHM_REGION_SIZE    (SHM_LANES_OFF + SHM_MAX_LANES * SHM_LANE_SIZE)

//--- Header offsets
#define SHM_OFF_MAGIC      0    // uint
#define SHM_OFF_VERSION    4    // uint
#define SHM_OFF_CAPACITY   8    // uint  (so slot / lane)
#define SHM_OFF_SLOTSIZE   12   // uint
#define SHM_OFF_MAXLANES   16   // uint
#define SHM_OFF_CREATETICK 24   // ulong

//--- Slot offsets (trong 1 slot 1024 byte)
#define SHM_SLOT_OFF_SEQBEGIN 0     // ulong
#define SHM_SLOT_OFF_LEN      8     // uint
#define SHM_SLOT_OFF_PAYLOAD  16    // uchar[1000]
#define SHM_SLOT_OFF_SEQEND   (SHM_SLOT_SIZE - 8) // ulong
#define SHM_PAYLOAD_MAX       (SHM_SLOT_OFF_SEQEND - SHM_SLOT_OFF_PAYLOAD)  // 1000

//+------------------------------------------------------------------+
//| Win32 imports.                                                    |
//|                                                                   |
//| MQL khong co #ifdef theo bitness nen phai khai bao CA HAI bo       |
//| 32/64-bit; nhanh khong dung van phai bien dich duoc. Chon nhanh    |
//| luc chay bang TerminalInfoInteger(TERMINAL_X64), dung dung mau     |
//| SOCKET_HANDLE32/64 cua ClientSocket.mqh.                          |
//|                                                                   |
//| CHI import nhung gi kernel32 THUC SU export. Da kiem tra bang     |
//| dumpbin: RtlMoveMemory co (ordinal 1249), CreateFileMappingW /     |
//| MapViewOfFile / UnmapViewOfFile / CloseHandle co. Cac ham          |
//| Interlocked* (tru SList) KHONG co tren x64 -> khong duoc import.   |
//+------------------------------------------------------------------+
#import "kernel32.dll"
//--- 64-bit (MT5)
long  CreateFileMappingW(long hFile, long lpAttr, uint prot, uint hi, uint lo, string name);
ulong MapViewOfFile(long hMap, uint access, uint offHi, uint offLo, ulong bytes);
int   UnmapViewOfFile(ulong base);
int   CloseHandle(long h);
//--- 32-bit (MT4). Khai bao trung ten voi ban 64-bit la CACH CHINH THUC cua
//--- MQL5 de ho tro ca hai bitness (tai lieu #import dung dung mau nay cho
//--- MessageBoxW). Trinh bien dich chon overload theo KIEU cua doi so, nen
//--- moi loi goi PHAI dung bien co kieu tuong minh (long cho 64, int cho 32)
//--- - dung hang so tran nhu 0 hay (-1) se de compiler tu suy kieu va co the
//--- chon nham nhanh 32-bit tren x64, lam handle bi cat ngan.
int   CreateFileMappingW(int hFile, int lpAttr, uint prot, uint hi, uint lo, string name);
uint  MapViewOfFile(int hMap, uint access, uint offHi, uint offLo, uint bytes);
int   UnmapViewOfFile(uint base);
int   CloseHandle(int h);
//--- Luu y: KHONG import OpenFileMappingW. Hai ban 32/64-bit cua no co
//--- danh sach tham so GIONG NHAU (uint,int,string) va chi khac kieu tra
//--- ve - dieu MQL khong cho phep (error 163). Khong can den no vi
//--- CreateFileMappingW da tra ve handle cua vung DA CO neu ton tai.

//--- Copy bo nho. HUONG do chu ky quyet dinh (xem canh bao #1 dau file).
//    Ban 64-bit nhan dia chi ulong, ban 32-bit nhan uint. Vi uint va ulong
//    deu la so nguyen khong dau, moi loi goi PHAI truyen bien co kieu dung
//    (m_base64 la ulong, m_base32 la uint) de compiler chon dung nhanh.
//    shm -> MQL (array/scalar la dich, truyen theo tham chieu)
void  RtlMoveMemory(uchar &dst[], ulong src, int len);
void  RtlMoveMemory(uint  &dst,   ulong src, int len);
void  RtlMoveMemory(ulong &dst,   ulong src, int len);
//    MQL -> shm (pointer la dich, truyen theo gia tri)
void  RtlMoveMemory(ulong dst, const uchar &src[], int len);
void  RtlMoveMemory(ulong dst, const uint  &src,   int len);
void  RtlMoveMemory(ulong dst, const ulong &src,   int len);
//    Ban 32-bit (MT4)
void  RtlMoveMemory(uchar &dst[], uint src, int len);
void  RtlMoveMemory(uint  &dst,   uint src, int len);
void  RtlMoveMemory(ulong &dst,   uint src, int len);
void  RtlMoveMemory(uint dst, const uchar &src[], int len);
void  RtlMoveMemory(uint dst, const uint  &src,   int len);
void  RtlMoveMemory(uint dst, const ulong &src,   int len);

//--- CHU Y: goi ham nay PHAI ghi ro scope 'kernel32::'. MQL5 co san mot ham
//--- GetLastError() tra ve _LastError (ma loi CUA MQL, khong lien quan gi
//--- den Win32), va theo thu tu uu tien cua trinh bien dich thi ham co san
//--- duoc chon TRUOC ham #import. Goi khong ghi scope se doc nham bien cua
//--- MQL: trieu chung la Win32 API tra ve 0 (that bai) nhung err van = 0 -
//--- dieu khong bao gio xay ra voi GetLastError() that cua Windows.
uint  GetLastError(void);
//--- KHONG import GetTickCount64: MQL5 da co san ham cung ten, cung y nghia
//--- (mili-giay tu luc khoi dong may) va ham co san se duoc uu tien.
#import

//+------------------------------------------------------------------+
//| CShmRegion - mot vung shared memory (mot writer duy nhat)         |
//+------------------------------------------------------------------+
class CShmRegion
  {
private:
   long              m_handle64;
   ulong             m_base64;
   int               m_handle32;
   uint              m_base32;
   bool              m_x64;
   bool              m_attached;
   bool              m_created;
   int               m_lastError;
   string            m_name;

public:
                     CShmRegion(void);
                    ~CShmRegion(void);

   bool              Attach(const string fullName);
   void              Detach(void);
   bool              IsAttached(void) const { return m_attached; }
   bool              IsCreator(void)  const { return m_created; }
   int               LastError(void)  const { return m_lastError; }
   string            Name(void)       const { return m_name; }

   //--- doc/ghi co dinh huong
   void              ReadBytes(const uint off, uchar &dst[], const int len);
   void              WriteBytes(const uint off, const uchar &src[], const int len);
   uint              ReadU32(const uint off);
   void              WriteU32(const uint off, const uint v);
   ulong             ReadU64(const uint off);
   void              WriteU64(const uint off, const ulong v);
  };

//+------------------------------------------------------------------+
CShmRegion::CShmRegion(void)
  {
   m_handle64  = 0;
   m_base64    = 0;
   m_handle32  = 0;
   m_base32    = 0;
   m_x64       = (bool)TerminalInfoInteger(TERMINAL_X64);
   m_attached  = false;
   m_created   = false;
   m_lastError = 0;
   m_name      = "";
  }

//+------------------------------------------------------------------+
CShmRegion::~CShmRegion(void)
  {
   Detach();
  }

//+------------------------------------------------------------------+
void CShmRegion::Detach(void)
  {
   if(m_x64)
     {
      if(m_base64   != 0) { UnmapViewOfFile(m_base64); m_base64   = 0; }
      if(m_handle64 != 0) { CloseHandle(m_handle64);   m_handle64 = 0; }
     }
   else
     {
      if(m_base32   != 0) { UnmapViewOfFile(m_base32); m_base32   = 0; }
      if(m_handle32 != 0) { CloseHandle(m_handle32);   m_handle32 = 0; }
     }
   m_attached = false;
  }

//+------------------------------------------------------------------+
//| CreateFileMappingW tra ve handle cua vung DA CO neu ten da ton tai |
//| (GetLastError == ERROR_ALREADY_EXISTS), nho vay moi process deu goi |
//| duoc cung mot ham: ai vao truoc thi tao, ai sau thi dung lai.      |
//+------------------------------------------------------------------+
bool CShmRegion::Attach(const string fullName)
  {
   Detach();
   m_name = fullName;

   if(m_x64)
     {
      //--- bien co kieu TUONG MINH: khong de lai hang so nao cho MQL phai
      //--- tu suy kieu (do chinh la cho da sinh ra bug chon nham overload).
      long hFile = (long)SHM_INVALID_HANDLE_VALUE;
      long attr  = 0;
      m_handle64 = CreateFileMappingW(hFile, attr, SHM_PAGE_READWRITE,
                                      0, (uint)SHM_REGION_SIZE, fullName);
      //--- PHAI doc kernel32::GetLastError() NGAY: moi lenh sau do deu co the ghi de no.
      uint err = kernel32::GetLastError();
      if(m_handle64 == 0)
        {
         m_lastError = (int)err;
         return false;
        }
      m_created = (err != SHM_ERROR_ALREADY_EXISTS);
      m_base64  = MapViewOfFile(m_handle64, SHM_FILE_MAP_ALL_ACCESS, 0, 0, (ulong)SHM_REGION_SIZE);
      if(m_base64 == 0)
        {
         m_lastError = (int)kernel32::GetLastError();
         CloseHandle(m_handle64);
         m_handle64 = 0;
         return false;
        }
     }
   else
     {
      int hFile = (int)SHM_INVALID_HANDLE_VALUE;
      int attr  = 0;
      m_handle32 = CreateFileMappingW(hFile, attr, SHM_PAGE_READWRITE,
                                        0, (uint)SHM_REGION_SIZE, fullName);
      uint err = kernel32::GetLastError();
      if(m_handle32 == 0)
        {
         m_lastError = (int)err;
         return false;
        }
      m_created = (err != SHM_ERROR_ALREADY_EXISTS);
      m_base32  = MapViewOfFile(m_handle32, SHM_FILE_MAP_ALL_ACCESS, 0, 0, (uint)SHM_REGION_SIZE);
      if(m_base32 == 0)
        {
         m_lastError = (int)kernel32::GetLastError();
         CloseHandle(m_handle32);
         m_handle32 = 0;
         return false;
        }
     }

   m_attached = true;
   return true;
  }

//+------------------------------------------------------------------+
//| Cac wrapper doc/ghi. Moi ham chi goi DUNG mot huong RtlMoveMemory. |
//+------------------------------------------------------------------+
void CShmRegion::ReadBytes(const uint off, uchar &dst[], const int len)
  {
   if(!m_attached || len <= 0) return;
   if(ArraySize(dst) < len) ArrayResize(dst, len);   // bat buoc: canh bao #2
   if(m_x64) RtlMoveMemory(dst, m_base64 + (ulong)off, len);
   else      RtlMoveMemory(dst, m_base32 + (uint)off, len);
  }

void CShmRegion::WriteBytes(const uint off, const uchar &src[], const int len)
  {
   if(!m_attached || len <= 0) return;
   if(m_x64) RtlMoveMemory(m_base64 + (ulong)off, src, len);
   else      RtlMoveMemory(m_base32 + (uint)off, src, len);
  }

uint CShmRegion::ReadU32(const uint off)
  {
   uint v = 0;
   if(!m_attached) return 0;
   if(m_x64) RtlMoveMemory(v, m_base64 + (ulong)off, 4);
   else      RtlMoveMemory(v, m_base32 + (uint)off, 4);
   return v;
  }

void CShmRegion::WriteU32(const uint off, const uint v)
  {
   if(!m_attached) return;
   if(m_x64) RtlMoveMemory(m_base64 + (ulong)off, v, 4);
   else      RtlMoveMemory(m_base32 + (uint)off, v, 4);
  }

ulong CShmRegion::ReadU64(const uint off)
  {
   ulong v = 0;
   if(!m_attached) return 0;
   if(m_x64) RtlMoveMemory(v, m_base64 + (ulong)off, 8);
   else      RtlMoveMemory(v, m_base32 + (uint)off, 8);
   return v;
  }

void CShmRegion::WriteU64(const uint off, const ulong v)
  {
   if(!m_attached) return;
   if(m_x64) RtlMoveMemory(m_base64 + (ulong)off, v, 8);
   else      RtlMoveMemory(m_base32 + (uint)off, v, 8);
  }

//+------------------------------------------------------------------+
//| CSharedMemRing                                                    |
//|                                                                   |
//| Thay the ClientSocket, giu nguyen bo API cac EA dang dung:         |
//|   IsSocketConnected() / Send() / Receive()                        |
//| nho vay phan logic trade trong EA gan nhu khong doi.              |
//|                                                                   |
//| MO HINH (v4)                                                      |
//|   Ca room dung DUNG MOT vung:  Local\CopyTradeRoom_<roomId>        |
//|   Ten vung CHI phu thuoc roomId - khong phu thuoc uid cua ai ca.   |
//|   Nho vay hai terminal chi can nhap CUNG roomId la thay nhau,      |
//|   khong con canh moi ben tu tinh ra mot ten vung khac nhau.        |
//|                                                                   |
//|   Ben trong vung chia SHM_MAX_LANES lane. Khi Send() lan dau, EA   |
//|   chiem mot lane va ghi danh uid vao REGISTRY. Tu do EA chi ghi    |
//|   vao lane cua rieng no => van la 1 writer/lane => KHONG can       |
//|   atomic (kernel32 x64 khong export Interlocked*, xem dau file).   |
//|                                                                   |
//|   Reader doc TAT CA lane tru lane cua chinh minh, moi lane mot     |
//|   cursor rieng, roi tron ket qua. Nho REGISTRY nen reader biet     |
//|   lane nao dang co chu ma khong can liet ke ten vung (Windows      |
//|   khong cho liet ke ten shared memory).                            |
//|                                                                   |
//| Cach dung:                                                        |
//|   MASTER    : Open(roomId, uid, generation) roi Send(...)          |
//|   SLAVE     : Open(roomId, 0, 0) roi Receive("")                   |
//|   HAI CHIEU : Open(roomId, uid, generation), Send va Receive       |
//+------------------------------------------------------------------+
class CSharedMemRing
  {
private:
   string            m_room;        // roomId - dinh danh phong, dung chung
   uint              m_writerUid;   // uid so, bat buoc khac 0 neu co ghi
   ulong             m_generation;  // dinh danh instance writer hien tai

   CShmRegion        m_rg;          // MOT vung duy nhat cho ca room
   bool              m_attached;

   int               m_myLane;      // lane ta ghi (-1 = chi doc)

   //--- cursor rieng cua process nay cho tung lane dang doc
   ulong             m_cursor[SHM_MAX_LANES];
   bool              m_primed[SHM_MAX_LANES];

   ulong             m_gapCount;
   ulong             m_lastScan;
   int               m_lastError;
   bool              m_opened;

   string            RegionName(void) const
     { return "Local\\CopyTradeRoom_" + m_room; }

   //--- offset cua entry REGISTRY thu lane
   uint              RegOffset(const int lane) const
     { return (uint)(SHM_REG_OFF + lane * SHM_REG_ENTRY_SIZE); }

   //--- offset slot thu seq trong lane
   uint              SlotOffset(const int lane, const ulong seq) const
     { return (uint)(SHM_LANES_OFF + lane * SHM_LANE_SIZE
                     + (uint)(seq & SHM_CAPACITY_MASK) * SHM_SLOT_SIZE); }

   bool              InitHeader(void);
   bool              CheckHeader(void);
   int               ClaimLane(void);
   string            DrainLane(const int lane);

public:
                     CSharedMemRing(void);
                    ~CSharedMemRing(void);

   //--- room = roomId (ca hai ben nhap giong nhau).
   //--- writerUid = uid cua writer, truyen 0 neu chi doc.
   //--- generation phai doi moi moi lan process/EA khoi dong.
   bool              Open(const string room, const uint writerUid,
                          const ulong generation = 0);
   void              Close(void);

   //--- API tuong thich ClientSocket
   bool              IsSocketConnected(void) const { return m_opened; }
   bool              IsReady(void) const { return m_opened; }
   int               GetLastSocketError(void)const { return m_lastError; }
   bool              Send(const string msg);
   string            Receive(const string sep = "");

   //--- bo sung
   ulong             GapCount(void)  const { return m_gapCount; }
   int               WriterCount(void);
   ulong             WriteSeq(void);
   void              TouchHeartbeat(void);
   string            RoomName(void) const  { return m_room; }
   int               MyLane(void)   const  { return m_myLane; }
   uint              WriterUid(void) const { return m_writerUid; }
   ulong             Generation(void) const { return m_generation; }
   void              SkipToLatest(void);
  };

//+------------------------------------------------------------------+
CSharedMemRing::CSharedMemRing(void)
  {
   m_room      = "";
   m_writerUid = 0;
   m_generation = 0;
   m_attached  = false;
   m_myLane    = -1;
   m_gapCount  = 0;
   m_lastScan  = 0;
   m_lastError = 0;
   m_opened    = false;
   for(int i = 0; i < SHM_MAX_LANES; i++)
     {
      m_cursor[i] = 0;
      m_primed[i] = false;
     }
  }

//+------------------------------------------------------------------+
CSharedMemRing::~CSharedMemRing(void)
  {
   Close();
  }

//+------------------------------------------------------------------+
//| Nha lane truoc khi go vung, de EA khac dung lai duoc.             |
//+------------------------------------------------------------------+
void CSharedMemRing::Close(void)
  {
   if(m_attached && m_myLane >= 0)
     {
      uint ro = RegOffset(m_myLane);
      m_rg.WriteU64(ro + SHM_REG_OFF_HEARTBEAT, 0);
      m_rg.WriteU64(ro + SHM_REG_OFF_GENERATION, 0);
      m_rg.WriteU32(ro + SHM_REG_OFF_UID,     0);
      m_rg.WriteU32(ro + SHM_REG_OFF_CLAIMED, 0);   // nha SAU CUNG
     }
   if(m_attached) { m_rg.Detach(); m_attached = false; }
   m_myLane = -1;
   m_writerUid = 0;
   m_generation = 0;
   m_opened = false;
  }

//+------------------------------------------------------------------+
//| Mo room. Ten vung CHI phu thuoc roomId.                           |
//+------------------------------------------------------------------+
bool CSharedMemRing::Open(const string room, const uint writerUid,
                          const ulong generation = 0)
  {
   Close();
   m_room      = room;
   m_writerUid = writerUid;
   m_generation = generation;
   m_lastError = 0;

   if(StringLen(room) == 0)
     {
      Print("SHM: thieu roomId");
      m_lastError = -10;
      return false;
     }

   if(!m_rg.Attach(RegionName()))
     {
      m_lastError = m_rg.LastError();
      Print("SHM: khong gan duoc vung '", RegionName(), "' err=", m_lastError);
      return false;
     }
   m_attached = true;

   if(m_rg.IsCreator())
     {
      if(!InitHeader())
        {
         Close();
         return false;
        }
      Print("SHM: vua tao vung '", RegionName(), "'");
     }
   else if(!CheckHeader())
     {
      Print("SHM: vung '", RegionName(), "' khong san sang (err=", m_lastError, ")");
      Close();
      return false;
     }

   //--- chi chiem lane khi thuc su co ghi
   if(writerUid > 0)
     {
      if(m_generation == 0)
         m_generation = GetTickCount64();
      if(m_generation == 0)
         m_generation = 1;

      m_myLane = ClaimLane();
      if(m_myLane < 0)
        {
         Print("SHM: het lane trong room '", m_room, "' (toi da ",
               SHM_MAX_LANES, " writer)");
         m_lastError = -3;
         Close();
         return false;
        }
      Print("SHM: room '", m_room, "' lane ", m_myLane, " uid=", m_writerUid,
            " generation=", m_generation);
     }
   else
      Print("SHM: room '", m_room, "' che do chi doc");

   //--- Dat moc doc NGAY luc Open. Message ghi sau Open se khong bi lan doc
   //--- dau tien nuot mat khi DrainLane prime cursor.
   SkipToLatest();
   m_lastScan = GetTickCount64();
   m_opened   = true;
   TouchHeartbeat();
   return true;
  }

//+------------------------------------------------------------------+
//| Khoi tao header. Vung moi luon duoc kernel zero-fill nen REGISTRY  |
//| va cac lane da sach; chi can ghi cac field mo ta, MAGIC ghi SAU    |
//| CUNG vi no la co "header san sang".                                |
//+------------------------------------------------------------------+
bool CSharedMemRing::InitHeader(void)
  {
   m_rg.WriteU32(SHM_OFF_CAPACITY,   SHM_CAPACITY);
   m_rg.WriteU32(SHM_OFF_SLOTSIZE,   SHM_SLOT_SIZE);
   m_rg.WriteU32(SHM_OFF_MAXLANES,   SHM_MAX_LANES);
   m_rg.WriteU64(SHM_OFF_CREATETICK, GetTickCount64());
   m_rg.WriteU32(SHM_OFF_VERSION,    SHM_VERSION);
   m_rg.WriteU32(SHM_OFF_MAGIC,      SHM_MAGIC);
   return true;
  }

//+------------------------------------------------------------------+
bool CSharedMemRing::CheckHeader(void)
  {
   uint magic = m_rg.ReadU32(SHM_OFF_MAGIC);
   if(magic != SHM_MAGIC)
     {
      m_lastError = -1;
      return false;
     }
   uint ver = m_rg.ReadU32(SHM_OFF_VERSION);
   if(ver != SHM_VERSION)
     {
      Print("SHM: sai phien ban layout o '", RegionName(), "': vung=", ver,
            " EA=", SHM_VERSION, ". Dong TAT CA terminal roi mo lai.");
      m_lastError = -2;
      return false;
     }
   if(m_rg.ReadU32(SHM_OFF_CAPACITY) != SHM_CAPACITY ||
      m_rg.ReadU32(SHM_OFF_SLOTSIZE) != SHM_SLOT_SIZE ||
      m_rg.ReadU32(SHM_OFF_MAXLANES) != SHM_MAX_LANES)
     {
      Print("SHM: layout v", SHM_VERSION, " khong khop o '", RegionName(), "'.");
      m_lastError = -4;
      return false;
     }
   return true;
  }

//+------------------------------------------------------------------+
//| Chiem mot lane de ghi.                                            |
//|                                                                   |
//| Neu uid cua ta DA co trong REGISTRY (EA restart, hoac Close()      |
//| khong kip chay vi terminal bi kill) thi dung lai chinh lane do -   |
//| nho vay so record khong bi nhay lui va reader khong thay "vung bi  |
//| tao lai".                                                          |
//|                                                                   |
//| KHONG co atomic nen viec chiem lane khong the tuyet doi an toan    |
//| neu hai EA khoi dong dung cung mot micro-giay. Ta giam thieu bang  |
//| cach ghi CLAIMED roi ghi UID, sau do doc lai kiem tra: neu uid     |
//| khong con la cua ta thi co nguoi khac vua chiem, thu lane ke tiep. |
//| Day la truong hop cuc ky hiem (EA khoi dong cach nhau vai ms la du |
//| tranh) va hau qua chi la mot lane bi bo trong, khong mat record.   |
//+------------------------------------------------------------------+
int CSharedMemRing::ClaimLane(void)
  {
   //--- vong 1: tim lane cu cua chinh uid nay
   if(m_writerUid != 0)
     {
      for(int i = 0; i < SHM_MAX_LANES; i++)
        {
         uint ro = RegOffset(i);
         if(m_rg.ReadU32(ro + SHM_REG_OFF_CLAIMED) == 1 &&
            m_rg.ReadU32(ro + SHM_REG_OFF_UID) == m_writerUid)
           {
            m_rg.WriteU64(ro + SHM_REG_OFF_HEARTBEAT, GetTickCount64());
            m_rg.WriteU64(ro + SHM_REG_OFF_GENERATION, m_generation);
            return i;
           }
        }
     }

   //--- vong 2: tim lane trong
   for(int i = 0; i < SHM_MAX_LANES; i++)
     {
      uint ro = RegOffset(i);
      if(m_rg.ReadU32(ro + SHM_REG_OFF_CLAIMED) != 0)
         continue;

      m_rg.WriteU32(ro + SHM_REG_OFF_CLAIMED, 1);
      m_rg.WriteU32(ro + SHM_REG_OFF_UID,     m_writerUid);
      m_rg.WriteU64(ro + SHM_REG_OFF_HEARTBEAT, GetTickCount64());
      m_rg.WriteU64(ro + SHM_REG_OFF_GENERATION, m_generation);

      //--- doc lai: co ai vua gianh mat khong?
      if(m_rg.ReadU32(ro + SHM_REG_OFF_UID) == m_writerUid)
         return i;
     }
   return -1;
  }

//+------------------------------------------------------------------+
//| Publish mot record vao lane cua rieng EA nay.                     |
//|                                                                   |
//| Thu tu ghi la PHAN QUAN TRONG NHAT:                               |
//|   1. seq_begin = seq   (danh dau "dang ghi")                       |
//|   2. len + payload                                                 |
//|   3. seq_end   = seq   (danh dau "da xong")                        |
//|   4. write_seq = seq+1 (cong bo cho reader)                        |
//+------------------------------------------------------------------+
bool CSharedMemRing::Send(const string msg)
  {
   if(!m_attached || m_myLane < 0) return false;

   //--- EA cu goi Send(msg + "\n"); framing bang len nen bo newline
   string clean = msg;
   StringReplace(clean, "\r", "");
   StringReplace(clean, "\n", "");
   if(StringLen(clean) == 0) return true;

   uchar buf[];
   int n = StringToCharArray(clean, buf, 0, WHOLE_ARRAY, CP_UTF8) - 1;  // bo NUL
   if(n <= 0) return true;
   if(n > SHM_PAYLOAD_MAX)
     {
      Print("SHM: tu choi record ", n, " byte > toi da ", SHM_PAYLOAD_MAX);
      m_lastError = -5;
      return false;
     }

   uint  ro   = RegOffset(m_myLane);
   ulong seq  = m_rg.ReadU64(ro + SHM_REG_OFF_WRITESEQ);
   uint  soff = SlotOffset(m_myLane, seq);

   m_rg.WriteU64(soff + SHM_SLOT_OFF_SEQBEGIN, seq);
   m_rg.WriteU32(soff + SHM_SLOT_OFF_LEN,      (uint)n);
   m_rg.WriteBytes(soff + SHM_SLOT_OFF_PAYLOAD, buf, n);
   m_rg.WriteU64(soff + SHM_SLOT_OFF_SEQEND,   seq);

   m_rg.WriteU64(ro + SHM_REG_OFF_WRITESEQ, seq + 1);
   m_rg.WriteU64(ro + SHM_REG_OFF_HEARTBEAT, GetTickCount64());
   return true;
  }

//+------------------------------------------------------------------+
//| Cap nhat lease cua writer ngay ca khi khong gui message.          |
//+------------------------------------------------------------------+
void CSharedMemRing::TouchHeartbeat(void)
  {
   if(!m_attached || m_myLane < 0 || !m_opened) return;
   uint ro = RegOffset(m_myLane);
   m_rg.WriteU64(ro + SHM_REG_OFF_GENERATION, m_generation);
   m_rg.WriteU64(ro + SHM_REG_OFF_HEARTBEAT, GetTickCount64());
  }

//+------------------------------------------------------------------+
//| Bo qua toan bo lich su o moi lane dang co chu.                    |
//|                                                                   |
//| Lane duoc chiem SAU loi goi nay khong bi anh huong, nhung khong    |
//| sao: DrainLane() luon prime cursor tai head o lan doc dau tien cua |
//| moi lane, nen lich su cu khong bao gio bi phat lai.               |
//+------------------------------------------------------------------+
void CSharedMemRing::SkipToLatest(void)
  {
   if(!m_attached) return;
   for(int i = 0; i < SHM_MAX_LANES; i++)
     {
      uint ro = RegOffset(i);
      m_cursor[i] = m_rg.ReadU64(ro + SHM_REG_OFF_WRITESEQ);
      m_primed[i] = true;
     }
  }

//+------------------------------------------------------------------+
//| So writer dang co trong room (ke ca chinh minh).                  |
//+------------------------------------------------------------------+
int CSharedMemRing::WriterCount(void)
  {
   if(!m_attached) return 0;
   int n = 0;
   for(int i = 0; i < SHM_MAX_LANES; i++)
      if(m_rg.ReadU32(RegOffset(i) + SHM_REG_OFF_CLAIMED) == 1)
         n++;
   return n;
  }

//+------------------------------------------------------------------+
//| So record lane cua chinh ta da ghi.                               |
//+------------------------------------------------------------------+
ulong CSharedMemRing::WriteSeq(void)
  {
   if(!m_attached || m_myLane < 0) return 0;
   return m_rg.ReadU64(RegOffset(m_myLane) + SHM_REG_OFF_WRITESEQ);
  }

//+------------------------------------------------------------------+
//| Lay record moi tu MOT lane, noi bang "\n".                        |
//+------------------------------------------------------------------+
string CSharedMemRing::DrainLane(const int lane)
  {
   uint  ro   = RegOffset(lane);
   ulong head = m_rg.ReadU64(ro + SHM_REG_OFF_WRITESEQ);

   if(!m_primed[lane])
     {
      //--- lan doc dau: bat dau tu head de khong phat lai lich su
      m_cursor[lane] = head;
      m_primed[lane] = true;
      return "";
     }

   if(head == m_cursor[lane]) return "";   // duong chay pho bien

   //--- lane duoc mot writer khac dung lai tu dau -> dat lai cursor
   if(head < m_cursor[lane])
     {
      Print("SHM: lane ", lane, " da reset (head=", head,
            " < cursor=", m_cursor[lane], "), dat lai cursor.");
      m_cursor[lane] = head;
      return "";
     }

   //--- reader tut lai qua xa -> slot cu da bi ghi de. Nhay len record cu
   //--- nhat con song va GHI LOG (mat tin hieu la su kien lien quan tien).
   if((head - m_cursor[lane]) > (ulong)SHM_CAPACITY)
     {
      ulong lost = (head - m_cursor[lane]) - (ulong)SHM_CAPACITY;
      m_gapCount++;
      Print("SHM CANH BAO: tut lai o lane ", lane, ", mat ", lost,
            " record. Tang SHM_CAPACITY neu tai dien.");
      m_cursor[lane] = head - (ulong)SHM_CAPACITY;
     }

   string result = "";
   uchar  payload[];
   int    got = 0;

   while(m_cursor[lane] < head && got < SHM_CAPACITY)
     {
      uint  soff = SlotOffset(lane, m_cursor[lane]);
      ulong sEnd = m_rg.ReadU64(soff + SHM_SLOT_OFF_SEQEND);

      //--- seqlock: seq_end phai dung bang seq dang cho.
      //--- Neu chua khop => writer dang ghi => DUNG lai, thu lai lan sau.
      //--- Khong nhay qua, vi nhay qua se lam MAT record.
      if(sEnd != m_cursor[lane])
         break;

      uint len = m_rg.ReadU32(soff + SHM_SLOT_OFF_LEN);
      if(len == 0 || len > (uint)SHM_PAYLOAD_MAX)
        {
         Print("SHM: bo qua slot lane ", lane, " seq=", m_cursor[lane],
               " len khong hop le (", len, ")");
         m_cursor[lane]++;
         continue;
        }

      m_rg.ReadBytes(soff + SHM_SLOT_OFF_PAYLOAD, payload, (int)len);

      //--- kiem tra seq_begin SAU khi copy: neu writer ghi de slot nay
      //--- trong luc ta doc thi hai gia tri se lech.
      ulong sBegin = m_rg.ReadU64(soff + SHM_SLOT_OFF_SEQBEGIN);
      if(sBegin != m_cursor[lane])
        {
         m_gapCount++;
         Print("SHM CANH BAO: record lane ", lane, " seq=", m_cursor[lane],
               " bi ghi de trong luc doc.");
         m_cursor[lane]++;
         continue;
        }

      if(StringLen(result) > 0) result += "\n";
      result += CharArrayToString(payload, 0, (int)len, CP_UTF8);

      m_cursor[lane]++;
      got++;
     }

   return result;
  }

//+------------------------------------------------------------------+
//| Gop record moi tu TAT CA lane tru lane cua chinh minh.            |
//|                                                                   |
//| Khong bo qua lane chua ghi danh: mot writer co the dang ghi do     |
//| dang khi ta quet. Ta cu doc theo write_seq cua tung lane; lane     |
//| trong thi write_seq = 0 nen khong ton gi.                          |
//+------------------------------------------------------------------+
string CSharedMemRing::Receive(const string sep = "")
  {
   if(!m_attached) return "";

   TouchHeartbeat();

   //--- Bao ve bo sung neu region bi thay doi/vo hieu sau khi Open().
   if(m_rg.ReadU32(SHM_OFF_MAGIC) != SHM_MAGIC)
     {
      ulong now = GetTickCount64();
      if((now - m_lastScan) < 2000)
         return "";
      m_lastScan = now;
      CheckHeader();
      return "";
     }

   string all = "";
   for(int i = 0; i < SHM_MAX_LANES; i++)
     {
      if(i == m_myLane) continue;      // khong doc lai chinh minh
      string part = DrainLane(i);
      if(StringLen(part) == 0) continue;
      if(StringLen(all) > 0) all += "\n";
      all += part;
     }
   return all;
  }

#endif // __SHAREDMEM_MQH__
//+------------------------------------------------------------------+
