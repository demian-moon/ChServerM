using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;


namespace EcsServerLibM
{


	//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>
	/// 설정 관련 INI 파일 클래스 - 클라, 서버 공용
	/// 최초 설정된 값으로 저장하고, 추후에는 ini파일 설정이 우선한다
	/// </summary>
	public abstract class IniOptionM
	{
		virtual protected string _iniFileName { get; set; }

		protected string _pathIni;

		protected const string sectNameBasic = "BasicSetting";

		const int DEFAULT_PORT = 7000; // 기본 포트

		// 기본 세팅
		static public string gIpAddress;
		static public int gPort;

		protected IniFileM _iniFile = new IniFileM();

		protected bool _bSaveFlag; // 변경점이 있어서 save해야 되는지 판단하는 플래그
		string _pathFile;

		// 상속 구현 //////////////////////////////////////////////////////
		virtual protected void SaveOptionSetting() { }
		virtual protected void LoadOptionSetting() { }
		//////////////////////////////////////////////////////////////////

		public IniOptionM(string srvIp = null, int port = 0, string pathFile = null)
		{
			_pathIni = GetPathToSave();

			if (pathFile == null)
				_pathFile = Path.Combine(_pathIni, _iniFileName);
			else
				_pathFile = pathFile;

			if (string.IsNullOrEmpty(srvIp) == false)
			{
				gIpAddress = srvIp;
			}
			else
			{
				gIpAddress = IPAddress.Loopback.ToString();
			}

			if (port != 0)
			{
				gPort = port;
			}
			else
			{
				gPort = DEFAULT_PORT;
			}
		}

		public string GetPathToSave()
		{
			return Environment.CurrentDirectory;
		}

		public void SaveCommonOptionSetting()
		{
			_iniFile[sectNameBasic]["IpAddress"] = gIpAddress;
			_iniFile[sectNameBasic]["Port"] = gPort;
		}
		/// <summary>
		/// 기본 옵션 세팅
		///  - ini 파일이 있을 때만 call 된다
		/// </summary>
		public void LoadCommonOptionSetting()
		{
			if (_iniFile[sectNameBasic].ContainsKey("IpAddress"))
			{
				string ipAddress = _iniFile[sectNameBasic]["IpAddress"].ToString();
				if (ipAddress != gIpAddress)
				{
					gIpAddress = ipAddress;
					_bSaveFlag = true;
				}
			}
			else // ini파일안에 설정이 없으면
			{
				gIpAddress = IPAddress.Loopback.ToString();
				_bSaveFlag = true;
			}

			if (_iniFile[sectNameBasic].ContainsKey("Port"))
			{
				int port = _iniFile[sectNameBasic]["Port"].ToInt();
				if (port != gPort)
				{
					gPort = port;
					_bSaveFlag = true;
				}
			}
			else // ini 파일안에 설정 없으면
			{
				gPort = DEFAULT_PORT;
				_bSaveFlag = true;
			}

		}

		void SaveIni()
		{
			SaveCommonOptionSetting();
			SaveOptionSetting();
			_iniFile.Save(_pathFile);
		}



		public bool IsIniFileExist()
		{
			if (File.Exists(_pathFile))
			{
				return true;
			}
			return false;
		}



		/// <summary>
		/// 1. ini 파일이 있으면 옵션을 로드한다
		///    - 생성자에 전달된 ip가 있으면 ip값을 업데이트 한다.
		/// 2. ini 파일이 없으면 
		///    - 생성자에 전달된 ip가 있으면 ip값을 set한다, 전달된 값이 null이면 루프백 ip를 설정한다.
		/// </summary>
		/// <returns></returns>
		public async ValueTask LoadIni()
		{

			if (gIpAddress != null) // ip 지정한 것이면
			{
				SaveIni();
				return;
			}

			if (IsIniFileExist() == true)    // Ip를 명시적으로 설정하면 저장하지 않는다
			{
				await _iniFile.Load(_pathFile).ConfigureAwait(false);
				LoadCommonOptionSetting();
				LoadOptionSetting();

				if (_bSaveFlag) // 변경점 있으면
					SaveIni();
			}
			else
			{
				SaveIni();
			}
		}
	}





}
