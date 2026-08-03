using FbsClassM;
using MongoDB.Bson;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EcsServerLibM
{
	public class SrvUserAuthM : IDB_DataM
	{
		public ObjectId DB_OBJECT_ID { get; set; }
		public string id;
		public string hashedPw;

		public SrvUserAuthM(ObjectId dbObjectId, string id, string pw)
		{
			this.id = id;
			this.hashedPw = pw;
			this.DB_OBJECT_ID = dbObjectId;
		}
	}
}
