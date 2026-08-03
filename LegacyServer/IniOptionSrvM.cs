namespace EcsServerLibM
{


	//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>
	/// 서버 설정 관련 INI 파일 클래스
	/// </summary>
	public class IniSrvOptionM : IniOptionM
	{
		protected override string _iniFileName { get => "OptionServerM.ini"; }

		const string sectNamePacketOpt = "PacketOption";


		public IniSrvOptionM(string srvIp = null, int port = 0, string pathFile = null) : base(srvIp, port, pathFile) { }


		override protected void SaveOptionSetting()
		{
			// 서버 패킷 동작 관련

		}

		override protected void LoadOptionSetting()
		{
			//if (_iniFile[sectNamePacketOpt].ContainsKey("DisconnectForceDelayMilliSec"))
			//{
			//    int disconnectForceDealyMs = _iniFile[sectNamePacketOpt]["DisconnectForceDelayMilliSec"].ToInt();
			//    if (disconnectForceDealyMs != gDisconnectForceDealyMs)
			//    {
			//        gDisconnectForceDealyMs = disconnectForceDealyMs;
			//        _bSaveFlag = true;
			//    }
			//}
		}
	}


}
