using EcsServerLibM;

namespace EcsClientLibM
{
	//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>
	/// 클라 설정 관련 INI 파일 클래스
	/// </summary>
	public class IniClntOptionM : IniOptionM
	{
		protected override string _iniFileName { get => "OptionClientM.ini"; }
		public IniClntOptionM(string srvIp = null, int port = 0, string pathFile = null) : base(srvIp, port, pathFile) { }

	}

}
