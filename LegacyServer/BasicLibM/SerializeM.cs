namespace EcsServerLibM.BasicLibM
{
	public class SerializeM
	{
		/// <summary>
		/// 스크럭쳐 Deserialize 참고 코드
		/// </summary>        
		/// <returns></returns>
		//public static PkHeadMOld? Deserialize(byte[] pkHeadByte)    //
		//{
		//    IntPtr pHeader = Marshal.AllocHGlobal(pkHeadLen);
		//    Marshal.Copy(pkHeadByte, 0, pHeader, pkHeadLen);

		//    PkHeadMOld? head = (PkHeadMOld?)Marshal.PtrToStructure(pHeader, typeof(PkHeadMOld));
		//    Marshal.FreeHGlobal(pHeader);

		//    return head;
		//}


		//static public T? Deserialize<T>(byte[] data)
		//{
		//    int size = Marshal.SizeOf(typeof(T));
		//    IntPtr ptr = Marshal.AllocHGlobal(size);
		//    Marshal.Copy(data, 0, ptr, size);

		//    T? st = (T?)Marshal.PtrToStructure<T>(ptr);
		//    Marshal.FreeHGlobal(ptr);
		//    return st;
		//}

		//static public byte[] Serialize<T>(T st)
		//{
		//    int size = Marshal.SizeOf(typeof(T));
		//    IntPtr ptr = Marshal.AllocHGlobal(size);
		//    Marshal.StructureToPtr<T>(st, ptr, true);

		//    byte[] data = new byte[size];
		//    Marshal.Copy(ptr, data, 0, size);
		//    Marshal.FreeHGlobal(ptr);

		//    return data;
		//}


		// FlatBuffer 패킷 Sereialize 참고 ---------------------

		//static public byte[] SerializePacket(uint pid, ushort ePacketType, byte[] sendData)
		//{
		//    var fbb = new FlatBufferBuilder(1);

		//    var osPkHead = FbsPkHeadM.CreateFbsPkHeadM(fbb, pid, gPkHeadLen, 0); // 첵섬 0
		//    var osConHead = FbsContentHeadM.CreateFbsContentHeadM(fbb, ePacketType, gContentHeadLen);
		//    var osConData = FbsPacketM.Create_contentDataVector(fbb, sendData);

		//    FbsPacketM.StartFbsPacketM(fbb);
		//    FbsPacketM.Add_pkHead(fbb, osPkHead);
		//    FbsPacketM.Add_contentHead(fbb, osConHead);
		//    FbsPacketM.Add_contentData(fbb, osConData);
		//    var osPacketM = FbsPacketM.EndFbsPacketM(fbb);
		//    fbb.Finish(osPacketM.Value);

		//    Memory<byte> mo = new Memory<byte>(fbb.SizedByteArray());

		//    mo = mo.Slice(gPkHeadLen);

		//    var hh = FbsPkHeadM.GetRootAsFbsPkHeadM(new ByteBuffer(mo.ToArray()));


		//    return fbb.SizedByteArray();
		//}



	}
}
