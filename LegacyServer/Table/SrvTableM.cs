using System.IO;
using System.Threading.Tasks;

namespace EcsServerLibM
{
	public class ServerConfigTableRuntimeM : LoadableDataInStructM
	{
		public string optionName;
		public string value0;
		public string value1;

	}


	public class ClientSettingTableRuntimeM : LoadableDataInStructM
	{
		public string optionName;
		public string value0;
		public string value1;

	}

	public class SrvTableM
	{
		public MetaDataM serverConfig;
		public MetaDataM clientSettings;
		public MetaDataM directScripts;

		public MetaDataRuntimeTableMan<ServerConfigTableRuntimeM> ServerConfigRuntimeMan { get; set; } // ServerConfig 런타임 테이블 매니져

		public MetaDataRuntimeTableMan<ClientSettingTableRuntimeM> ClientSettingRuntimeMan { get; set; } // ServerConfig 런타임 테이블 매니져

		public virtual ValueTask _MetaTableLoadingAsync(string metaDirPath)
		{
			return ValueTask.CompletedTask;
		}

		public async Task<SrvTableM> MetaTableLoadingAsync(string metaDirPath)
		{
			if (Directory.Exists(metaDirPath) == false)
				throw new DirectoryNotFoundException($"디렉토리 없음:{metaDirPath}");


			// 서버 설정 테이블 로딩(ServerConfig)            
			string fileName = @"SysTable\ServerConfig.smt";
			string filePath = Path.Combine(metaDirPath, fileName);

			// 서버 옵션
			serverConfig = await MetaDataM.GetMetaDataFromFileAsync(filePath).ConfigureAwait(false);
			ServerConfigRuntimeMan = new MetaDataRuntimeTableMan<ServerConfigTableRuntimeM>(serverConfig); // 런타임 테이블 매니져

			// 클라이언트 관련 옵션            
			fileName = @"SysTable\ClientSettings.smt";
			filePath = Path.Combine(metaDirPath, fileName);
			clientSettings = await MetaDataM.GetMetaDataFromFileAsync(filePath).ConfigureAwait(false);
			ClientSettingRuntimeMan = new MetaDataRuntimeTableMan<ClientSettingTableRuntimeM>(clientSettings);

            // 다이렉트 스크립트
            // 클라이언트 관련 옵션            
            fileName = @"SysTable\DirectScripts.smt";
            filePath = Path.Combine(metaDirPath, fileName);
            directScripts = await MetaDataM.GetMetaDataFromFileAsync(filePath).ConfigureAwait(false);

			            
            //////////////////////////////////////////////////////////////////
            // 메타테이블 데이터로 서버 글로벌 변수 세팅 - GlobalM 변수도 세팅            
            SrvGlobal.SetSrvGloalVariable(this);


			// 앱 메타테이블 데이터 로딩
			await _MetaTableLoadingAsync(metaDirPath).ConfigureAwait(false);

			return this;
		}
	}
}
