using Arch.Core;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ZstdSharp.Unsafe;

namespace EcsServerLibM
{


	public interface IDB_DataM
	{
		ObjectId DB_OBJECT_ID { get; set; }
	}

	/// <summary>
	///  몽고DB를 관리하는 클래스입니다.
	/// </summary>
	/// <typeparam name="T">실제 저장하고자 하는 컬렉션 타입</typeparam>
	public class MongoDBManagerM : IDisposable
	{
		MongoClient _client;
		private readonly IMongoDatabase _database;
		ConcurrentDictionary<string, object> _dicCollection = new();
		CancellationTokenSource _cts;
		private bool disposedValue;

		public MongoDBManagerM(string connectionString, string databaseName)
		{
			var settings = MongoClientSettings.FromConnectionString(connectionString);
			
			// 대용량 처리를 위한 설정
			settings.MaxConnectionPoolSize = 500;         // 높은 동시성
			settings.MinConnectionPoolSize = 50;          // 충분한 초기 연결
			settings.MaxConnectionIdleTime = TimeSpan.FromMinutes(10);
			settings.WaitQueueTimeout = TimeSpan.FromSeconds(10);

			// 응답성 향상
			settings.ConnectTimeout = TimeSpan.FromSeconds(5);
			settings.SocketTimeout = TimeSpan.FromSeconds(60);
			settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);

			_client = new MongoClient(settings);
			_database = _client.GetDatabase(databaseName);
			_cts = new CancellationTokenSource();
		}
			

		/// <summary>
		/// 몽고 클라이언트가 연결되어 있는지 확인 합니다.
		/// </summary>
		/// <param name="connectionString"></param>
		/// <returns></returns>
		public static async Task<bool> IsMongoDBConnectedAsync(string connectionString)
		{
			if (string.IsNullOrEmpty(connectionString))
				return false;

			try
			{
				var settings = MongoClientSettings.FromConnectionString(connectionString);
				settings.ConnectTimeout = TimeSpan.FromSeconds(5);
				settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);

				using var client = new MongoClient(settings);				

				var database = client.GetDatabase("admin");
				var pingCommand = new BsonDocument("ping", 1);

				await database.RunCommandAsync<BsonDocument>(pingCommand, null);
				return true;
			}
			catch (MongoException)
			{
				return false;
			}
			catch (TimeoutException)
			{
				return false;
			}
		}
		/// <summary>
		/// 대량의 데이터를 갱신할 때 table을 직접 얻는다
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="collectionName"></param>
		/// <returns></returns>
		public MongoDBCollectionM<T> GetOrCreateCollection<T>(string collectionName) where T : IDB_DataM
		{
			var key = $"{typeof(T).Name}_{collectionName}";
			return (MongoDBCollectionM<T>)_dicCollection.GetOrAdd(key, _ => new MongoDBCollectionM<T>(_database, collectionName));
		}

		/// <summary>
		/// 단일 데이터 삽입 할때 쓴다.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="tableName"></param>
		/// <param name="data"></param>
		/// <returns></returns>
		public async Task<bool> InsertAsync<T>(string tableName, T data) where T : IDB_DataM
		{
			var collection = GetOrCreateCollection<T>(tableName);
			return await collection.InsertAsync(data, _cts.Token).ConfigureAwait(false);
		}


		/// <summary>
		/// ObjectId로 데이터를 얻거나 저장하고 얻기
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="tableName"></param>
		/// <param name="objectId"></param>
		/// <returns></returns>
		public async Task<T> GetOrCreateAsync<T>(string tableName, ObjectId objectId, UpdateDefinition<T> updateDef) where T : IDB_DataM
		{
			var collection = GetOrCreateCollection<T>(tableName);
			return await collection.GetOrCreateAsync(objectId, updateDef, _cts.Token).ConfigureAwait(false);
		}


		public async Task<T> GetOrCreateAsync<T>(string tableName, FilterDefinition<T> filter, ProjectionDefinition<T> projection, UpdateDefinition < T> updateDef ) where T : IDB_DataM
		{
			var collection = GetOrCreateCollection<T>(tableName);
			
			return await collection.GetOrCreateAsync(filter, projection, updateDef, _cts.Token).ConfigureAwait(false);
		}


		/// <summary>
		/// ObjectId로 데이터를 업데이트하거나 추가하기
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="tableName"></param>
		/// <param name="objectId"></param>
		/// <param name="updateDef"></param>
		/// <returns></returns>
		public async Task<bool> UpsertAsync<T>(string tableName, ObjectId objectId, UpdateDefinition<T> updateDef) where T : IDB_DataM
		{
			var collection = GetOrCreateCollection<T>(tableName);
			return await collection.UpsertAsync(objectId, updateDef, _cts.Token).ConfigureAwait(false);
		}

		/// <summary>
		/// ObjectId로 데이터를 얻기
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="tableName"></param>
		/// <param name="objectId"></param>
		/// <returns></returns>
		public async Task<T> GetAsync<T>(string tableName, ObjectId objectId) where T : IDB_DataM
		{
			var collection = GetOrCreateCollection<T>(tableName);
			return await collection.GetAsync(objectId, _cts.Token).ConfigureAwait(false);
		}

		/// <summary>
		/// 필터 정의 프로젝션을 사용해서 데이터를 얻기
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="tableName"></param>
		/// <param name="filter"></param>
		/// <param name="projection">프로젝션을 사용하면 포함되지 않는 필드는 default 값임</param>
		/// <returns></returns>
		public async Task<T> GetAsync<T>(string tableName, FilterDefinition<T> filter, ProjectionDefinition<T> projection = null) where T : IDB_DataM
		{
			var collection = GetOrCreateCollection<T>(tableName);
			return await collection.GetAsync(filter, projection, _cts.Token).ConfigureAwait(false);
		}
		

		/// <summary>
		/// ObjectId로 데이터가 있는지 검사합니다.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="tableName">테이블 이름</param>
		/// <param name="objectId">오브젝트 id</param>
		/// <returns></returns>
		public async Task<bool> HasAasync<T>(string tableName, ObjectId objectId) where T : IDB_DataM
		{
			var collection = GetOrCreateCollection<T>(tableName);
			return await collection.HasAsync(objectId, _cts.Token).ConfigureAwait(false);
		}

		/// <summary>
		/// 필터 정의를 사용해서 데이터가 있는지 검사합니다.
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="tableName"></param>
		/// <param name="filter"></param>
		/// <returns></returns>
		public async Task<bool> HasAasync<T>(string tableName, FilterDefinition<T> filter) where T : IDB_DataM
		{
			var collection = GetOrCreateCollection<T>(tableName);
			return await collection.HasAsync(filter, _cts.Token).ConfigureAwait(false);
		}

		public async Task<bool> UpdateAsync<T>(string tableName, ObjectId objectId, UpdateDefinition<T> updateDef) where T : IDB_DataM
		{
			var collection = GetOrCreateCollection<T>(tableName);
			return await collection.UpdateAsync(objectId, updateDef, _cts.Token).ConfigureAwait(false);
		}


		

		protected virtual void Dispose(bool disposing)
		{
			if (!disposedValue)
			{
				if (disposing)
				{
					// TODO: 관리형 상태(관리형 개체)를 삭제합니다.
					_cts?.Cancel();
					Thread.Sleep(1000); // 잠시 대기해서 진행중인 작업들이 취소 신호를 받도록
					_cts?.Dispose();
				}

				// TODO: 비관리형 리소스(비관리형 개체)를 해제하고 종료자를 재정의합니다.
				// TODO: 큰 필드를 null로 설정합니다.
				disposedValue = true;
			}
		}

		// // TODO: 비관리형 리소스를 해제하는 코드가 'Dispose(bool disposing)'에 포함된 경우에만 종료자를 재정의합니다.
		// ~MongoDBManagerM()
		// {
		//     // 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
		//     Dispose(disposing: false);
		// }

		public void Dispose()
		{
			// 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
			Dispose(disposing: true);
			GC.SuppressFinalize(this);
		}


		//[BsonKnownTypes(typeof(ConcreteCharacter1), typeof(ConcreteCharacter2))]  <--- 명시적으로 파생 클래스 등록 하면 베이스클래스로 저장, 조회 가능
		//BsonClassMap.RegisterClassMap<AbCharacterDB_DataM>();  <-- 동적으로 등록
		//BsonClassMap.RegisterClassMap<ConcreteCharacter1>();
		//BsonClassMap.RegisterClassMap<ConcreteCharacter2>();




	}

	////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
	////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
	////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
	/// <summary>
	/// 몽고DB 컬렉션을 관리하는 클래스입니다.
	/// </summary>
	/// <typeparam name="T"></typeparam>
	///
	public class MongoDBCollectionM<T> where T : IDB_DataM
	{
		private readonly IMongoCollection<T> _collection;		

		public MongoDBCollectionM(IMongoDatabase database, string collectionName)
		{			
			_collection = database.GetCollection<T>(collectionName);
		}

		/// <summary>
		/// 유저 데이터 리스트를 저장
		/// </summary>
		/// <param name="datas"></param>
		/// <returns></returns>
		public async Task<bool> InsertAsync(List<T> datas, CancellationToken ct)
		{
			try
			{
				await _collection.InsertManyAsync(datas, null, ct).ConfigureAwait(false);
				Debug.WriteLine($"{datas.Count}개의 유저 데이터가 저장되었습니다.");
				return true;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"리스트 저장 오류: {ex.Message}");
				return false;
			}
		}

		// 2. 유저 데이터 하나만 저장
		public async Task<bool> InsertAsync(T data, CancellationToken ct)
		{
			try
			{
				await _collection.InsertOneAsync(data, null, ct).ConfigureAwait(false);
				Debug.WriteLine($"데이터가 저장되었습니다.");
				return true;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"데이터 저장 오류: {ex.Message}");
				return false;
			}
		}


		/// <summary>
		/// ObejctId로 데이터 있는지 검사
		/// </summary>
		/// <param name="objectId"></param>
		/// <returns></returns>
		public async Task<bool> HasAsync(ObjectId objectId, CancellationToken ct)
		{
			var filter = Builders<T>.Filter.Eq(dt => dt.DB_OBJECT_ID, objectId);

			return await HasAsync(filter, ct);
		}


		/// <summary>
		/// 필터 definition을 사용해서 데이터가 있는지 검사합니다.
		/// </summary>
		/// <param name="filter"></param>
		/// <returns></returns>
		public async Task<bool> HasAsync(FilterDefinition<T> filter, CancellationToken ct)
		{
			try
			{
				var count = await _collection.CountDocumentsAsync(filter, new CountOptions { Limit = 1 }, ct);
				return count > 0;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"HasAsync 버그 {ex.Message}");
				throw;
			}
		}


		// 5. 유저 데이터를 삭제
		public async Task<bool> DeleteAsync(ObjectId objectId, CancellationToken ct)
		{
			try
			{
				var filter = Builders<T>.Filter.Eq(data => data.DB_OBJECT_ID, objectId);
				var result = await _collection.DeleteOneAsync(filter, ct).ConfigureAwait(false);

				if (result.DeletedCount > 0)
				{
					Debug.WriteLine($"{objectId} - 몽고DB 데이터가 삭제되었습니다.");
					return true;
				}
				else
				{
					Debug.WriteLine("삭제할 데이터를 찾을 수 없습니다.");
					return false;
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"데이터 삭제 오류: {ex.Message}");
				throw;
			}
		}


		/// <summary>
		/// 데이터 리스트를 읽음
		/// </summary>
		/// <returns></returns>
		public async Task<List<T>> GetAllAsync(CancellationToken ct)
		{
			try
			{
				var datas = await _collection.Find(_ => true).ToListAsync(ct).ConfigureAwait(false);
				Debug.WriteLine($"{datas.Count}개의 데이터를 조회했습니다.");
				return datas;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"유저 리스트 조회 오류: {ex.Message}");
				throw;
			}
		}

		/// <summary>
		/// 데이터 하나만 읽음
		/// </summary>
		/// <param name="objectId"></param>
		/// <returns></returns>
		public async Task<T> GetOrCreateAsync(ObjectId objectId, UpdateDefinition<T> updateDef, CancellationToken ct)
		{			
			// 토큰이 안전한지 먼저 확인
			if (ct.IsCancellationRequested)
			{
				Debug.WriteLine("CancellationToken이 이미 취소되었습니다.");
				return default(T);
			}


			var filter = Builders<T>.Filter.Eq(dt => dt.DB_OBJECT_ID, objectId);
			var options = new FindOneAndUpdateOptions<T>
			{
				IsUpsert = true, // 없으면 새로 생성
				ReturnDocument = ReturnDocument.After // 업데이트된 문서 반환
			};

			int maxRetries = 3;
			for (int i = 0; i < maxRetries; i++)
			{
				try
				{
					var findData = await _collection.FindOneAndUpdateAsync(
					filter,
					updateDef,
					options,
					ct);


					return findData;

				}
				catch (OperationCanceledException ex) when (i < maxRetries)
				{
					if (i < maxRetries - 1)
					{
						Debug.WriteLine($"MongoCollectionM.GetOrCreateAsync 사용자 데이터 처리 중 오류가 발생했습니다.{ex.Message}");
						await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i))); // 지수 백오프						
					}
					else
						throw;
				}
				catch (MongoException ex) when (i < maxRetries)
				{
					// MongoDB 관련 예외 처리
					if (i < maxRetries - 1)
					{
						Debug.WriteLine($"MongoCollectionM.GetOrCreateAsync 사용자 데이터 처리 중 오류가 발생했습니다. {ex.Message}");
						await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i))); // 지수 백오프
					}
					throw;
				}
				catch (Exception ex) 
				{
					Debug.WriteLine($"MongoCollectionM.GetOrCreateAsync 오류 : {ex.Message}");
					throw;
				}
			}

			return default(T);			
		}


		/// <summary>
		/// 데이터 하나만 읽음
		/// </summary>
		/// <param name="objectId"></param>
		/// <returns></returns>
		public async Task<T> GetOrCreateAsync(FilterDefinition<T> filter, ProjectionDefinition<T> projection, UpdateDefinition<T> updateDef, CancellationToken ct)
		{
			// 토큰이 안전한지 먼저 확인
			if (ct.IsCancellationRequested)
			{
				Debug.WriteLine("CancellationToken이 이미 취소되었습니다.");
				return default(T);
			}

						
			var options = new FindOneAndUpdateOptions<T>
			{
				IsUpsert = true, // 없으면 새로 생성
				ReturnDocument = ReturnDocument.After, // 업데이트된 문서 반환
				Projection = projection				
			};

			int maxRetries = 3;
			for (int i = 0; i < maxRetries; i++)
			{
				try
				{
					var findData = await _collection.FindOneAndUpdateAsync(
					filter, 					
					updateDef,
					options,
					ct);

					return findData;
				}
				catch (OperationCanceledException ex) when (i < maxRetries)
				{
					if (i < maxRetries - 1)
					{
						Debug.WriteLine($"MongoCollectionM.GetOrCreateAsync 사용자 데이터 처리 중 오류가 발생했습니다.{ex.Message}");
						await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i))); // 지수 백오프						
					}
					else
						throw;
				}
				catch (MongoException ex) when (i < maxRetries)
				{
					// MongoDB 관련 예외 처리
					if (i < maxRetries - 1)
					{
						Debug.WriteLine($"MongoCollectionM.GetOrCreateAsync 사용자 데이터 처리 중 오류가 발생했습니다. {ex.Message}");
						await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i))); // 지수 백오프
					}
					throw;
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"MongoCollectionM.GetOrCreateAsync 오류 : {ex.Message}");
					throw;
				}
			}

			return default(T);


		}


		public async Task<bool> UpsertAsync(ObjectId objectId, UpdateDefinition<T> updateDef, CancellationToken ct)
		{
			var filter = Builders<T>.Filter.Eq(dt => dt.DB_OBJECT_ID, objectId);
			var options = new UpdateOptions<T>
			{
				IsUpsert = true, // 없으면 생성, 있으면 업데이트
				
			};

			var maxRetries = 5;
			for (int i = 0; i < maxRetries; i++)
			{
				try
				{
					var result = await _collection.UpdateOneAsync(filter, updateDef, options, ct);
					if (result.UpsertedId != null)
					{
						//Console.WriteLine("새 사용자가 생성되었습니다.");
						return true;
					}
					else if (result.ModifiedCount > 0)
					{
						//Console.WriteLine("기존 사용자가 업데이트되었습니다.");
						return true;
					}
				}
				catch (OperationCanceledException ex) when (i < maxRetries)
				{
					if (i < maxRetries - 1)
					{
						Debug.WriteLine($"MongoCollectionM.UpsertAsync 사용자 데이터 처리 중 오류가 발생했습니다.{ex.Message}");
						await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i))); // 지수 백오프						
					}
					else
						throw;
				}
				catch (MongoException ex) when (i < maxRetries)
				{
					// MongoDB 관련 예외 처리
					if (i < maxRetries - 1)
					{
						Debug.WriteLine($"MongoCollectionM.UpsertAsync 사용자 데이터 처리 중 오류가 발생했습니다. {ex.Message}");
						await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i))); // 지수 백오프
					}
					throw;
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"예외 : MongoCollectionM.UpsertAsync - {ex.Message}");
					throw;
				}
			}

			return false;
		}

		/// <summary>
		/// 데이터 하나만 읽음
		/// </summary>
		/// <param name="objectId"></param>
		/// <returns></returns>
		public async Task<T> GetAsync(ObjectId objectId, CancellationToken ct)
		{
			try
			{
				// 토큰이 안전한지 먼저 확인
				if (ct.IsCancellationRequested)
				{
					Debug.WriteLine("CancellationToken이 이미 취소되었습니다.");
					return default(T);
				}


				var filter = Builders<T>.Filter.Eq(dt => dt.DB_OBJECT_ID, objectId);
				var data = await _collection.Find(filter).FirstOrDefaultAsync(ct).ConfigureAwait(false);


				if (data != null)
				{
					Debug.WriteLine($"데이터 {objectId}를 조회했습니다.");
				}
				else
				{
					Debug.WriteLine("데이터를 찾을 수 없습니다.");
				}
				return data;

			}
			catch (Exception ex)
			{
				Debug.WriteLine($"유저 조회 오류: {ex.Message}");
				return default(T);
			}
		}

		public async Task<T> GetAsync(FilterDefinition<T> filter, ProjectionDefinition<T> projection = null, CancellationToken ct = default)
		{
			try
			{
				// 토큰이 안전한지 먼저 확인
				if (ct.IsCancellationRequested)
				{
					Debug.WriteLine("CancellationToken이 이미 취소되었습니다.");
					return default(T);
				}

				var query = _collection.Find(filter);
				if (projection != null)
					query = query.Project<T>(projection);				

				return await query.FirstOrDefaultAsync(ct).ConfigureAwait(false); ;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"유저 조회 오류: {ex.Message}");
				throw;
			}
		}

		/// <summary>
		/// 기본 필터 사용(id에 맞는 데이터 찾음)
		/// </summary>
		/// <param name="objectId"></param>
		/// <param name="updateDef"></param>
		/// <returns></returns>
		public async Task<bool> UpdateAsync(ObjectId objectId, UpdateDefinition<T> updateDef, CancellationToken ct)
		{

			var filter = Builders<T>.Filter.Eq(dt => dt.DB_OBJECT_ID, objectId);
			return await UpdateAsync(filter, updateDef, ct).ConfigureAwait(false);
		}


		/// <summary>
		/// 필터와 업데이트 정의를 사용해서 데이터를 갱신합니다.
		/// </summary>
		/// <param name="id"></param>
		/// <param name="filter"></param>
		/// <param name="updateDef"></param>
		/// <returns></returns>
		public async Task<bool> UpdateAsync(FilterDefinition<T> filter, UpdateDefinition<T> updateDef, CancellationToken ct)
		{
			try
			{
				var result = await _collection.UpdateOneAsync(filter, updateDef, null, ct).ConfigureAwait(false);

				if (result.ModifiedCount > 0)
				{
					Debug.WriteLine("데이터가 갱신되었습니다.");
					return true;
				}
				else
				{
					Debug.WriteLine("갱신할 데이터를 찾을 수 없습니다.");
					return false;
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"데이터 갱신 오류: {ex.Message}");
				throw;
			}
		}


		/// <summary>
		/// UpdateDefinition을 사용해서 데이터 리스트를 갱신합니다.
		/// </summary>
		/// <param name="datas"></param>
		/// <param name="updateDef"></param>
		/// <returns></returns>
		public async Task<bool> UpdateManyAsync(List<T> datas, UpdateDefinition<T> updateDef, CancellationToken ct)
		{
			var requests = datas.Select(data =>
				new UpdateOneModel<T>(Builders<T>.Filter.Eq(d => d.DB_OBJECT_ID, data.DB_OBJECT_ID), updateDef)
			).ToList();

			var result = await _collection.BulkWriteAsync(requests, null, ct).ConfigureAwait(false);
			return result.ModifiedCount == datas.Count;
		}

	}
}



