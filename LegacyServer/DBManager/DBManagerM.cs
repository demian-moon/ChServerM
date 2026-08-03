using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZstdSharp.Unsafe;

namespace EcsServerLibM
{
	public class DBManagerM
	{
		private static DBManagerM _instance;
		private static bool _initialized;
		private static object _syncLock;
		public readonly MongoDBManagerM _mongoDbMgr;
		public MongoDBManagerM DbMgr => _mongoDbMgr;
				
		private DBManagerM()
		{			
			// 초기화 로직
			_mongoDbMgr = new MongoDBManagerM(SrvGlobal.gDbConnectionString, "TangDB");
			ServerM.logM.Debug($"########################################################################짜장###################################################################");
		}

		public static DBManagerM Instance
		{
			get
			{
				return LazyInitializer.EnsureInitialized(
					ref _instance,
					ref _initialized,   // 옵션이 있으면 내부적으로 초기화 상태를 관리해서 더 효율적임
					ref _syncLock,
					() => new DBManagerM());
			}
		}				
	}
}
