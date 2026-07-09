// BinaryHelper.mqh
#property strict

class CBinaryHelper {
public:
   // Pack helpers
   static void PackInt32(uchar &buf[], int offset, int value) {
      struct Int32Pack { int v; } p;
      p.v = value;
      uchar tmp[];
      ArrayResize(tmp, 4);
      StructToCharArray(p, tmp, 0);
      ArrayCopy(buf, tmp, offset, 0, 4);
   }
   
   static void PackUInt64(uchar &buf[], int offset, ulong value) {
      struct UInt64Pack { ulong v; } p;
      p.v = value;
      uchar tmp[];
      ArrayResize(tmp, 8);
      StructToCharArray(p, tmp, 0);
      ArrayCopy(buf, tmp, offset, 0, 8);
   }
   
   static void PackDouble(uchar &buf[], int offset, double value) {
      struct DoublePack { double v; } p;
      p.v = value;
      uchar tmp[];
      ArrayResize(tmp, 8);
      StructToCharArray(p, tmp, 0);
      ArrayCopy(buf, tmp, offset, 0, 8);
   }

   static void PackString(uchar &buf[], int offset, string value, int fixedSize) {
      uchar tmp[];
      int len = StringToCharArray(value, tmp, 0, WHOLE_ARRAY, CP_UTF8);
      if(len > 0) len--; // bỏ null terminator từ StringToCharArray
      int copyLen = MathMin(len, fixedSize);
      if(copyLen > 0)
         ArrayCopy(buf, tmp, offset, 0, copyLen);
      // phần còn lại đã là 0 từ ArrayInitialize
   }
   
   // Unpack helpers
   static int UnpackInt32(uchar &buf[], int offset) {
      struct Int32Pack { int v; } p;
      uchar tmp[];
      ArrayResize(tmp, 4);
      ArrayCopy(tmp, buf, 0, offset, 4);
      CharArrayToStruct(p, tmp, 0);
      return p.v;
   }
   
   static ulong UnpackUInt64(uchar &buf[], int offset) {
      struct UInt64Pack { ulong v; } p;
      uchar tmp[];
      ArrayResize(tmp, 8);
      ArrayCopy(tmp, buf, 0, offset, 8);
      CharArrayToStruct(p, tmp, 0);
      return p.v;
   }
   
   static double UnpackDouble(uchar &buf[], int offset) {
      struct DoublePack { double v; } p;
      uchar tmp[];
      ArrayResize(tmp, 8);
      ArrayCopy(tmp, buf, 0, offset, 8);
      CharArrayToStruct(p, tmp, 0);
      return p.v;
   }
};
