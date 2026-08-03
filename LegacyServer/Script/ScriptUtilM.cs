using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EcsServerLibM
{
	public class ScriptMetaM
	{
		static ConcurrentDictionary<string, ScriptMetaM> scriptMetaCache = new();

		MetaDataM MetaData { get; set; } // 스크립트 메타 데이터

		ScriptMetaM(MetaDataM metaData)
		{
			MetaData = metaData;
		}

		/// <summary>
		/// ScriptMetaM 객체를 생성하는 함수입니다.
		/// </summary>
		/// <param name="scriptMetaFileName">스크립트 메타 파일이름</param>
		/// <returns></returns>
		static public ScriptMetaM CreateScriptMeta(string scriptMetaFileName)
		{
			if (string.IsNullOrEmpty(scriptMetaFileName))
				new ScriptMetaM(null);

			if (scriptMetaCache.TryGetValue(scriptMetaFileName, out var scriptMeta))
				return scriptMeta; // 캐시에서 찾으면 바로 리턴

			// 확장자 없으면
			if (scriptMetaFileName.EndsWith(".mt") == false)
			{
				scriptMetaFileName = new StringBuilder(scriptMetaFileName).Append(".mt").ToString();
			}

			string metaDir = AppDomain.CurrentDomain.BaseDirectory;
			string filePath = System.IO.Path.Combine(metaDir, @"ScriptMeta/" + scriptMetaFileName);

			var metaData = MetaDataM.GetMetaDataFromFile(filePath); // 비동기 함수를 쓰면 안된다.

			scriptMeta = new ScriptMetaM(metaData);
			scriptMetaCache.TryAdd(scriptMetaFileName, scriptMeta); // 캐시에 추가
			return scriptMeta;
		}

		/// <summary>
		/// /// 사용자 메타의 데이터를 가져오는 함수입니다.
		/// </summary>
		/// <param name="key">메타의 key</param>
		/// <param name="header">메타의 컬럼 헤더 문자열</param>
		/// <returns></returns>
		public string GetData(string key, string header)
		{
			return MetaData?.GetData(key, header);
		}

		/// <summary>
		/// 사용자 메타의 데이터를 가져오는 함수입니다.
		/// </summary>
		/// <param name="key">메타의 key</param>
		/// <param name="headerIndex">메타의 index 0부터 시작</param>
		/// <returns></returns>
		public string GetData(string key, int headerIndex)
		{
			return MetaData?.GetData(key, headerIndex);
		}
	}



	public static class ScriptUtilM
	{
		


		// 스크립트 메타
		

	}
}
