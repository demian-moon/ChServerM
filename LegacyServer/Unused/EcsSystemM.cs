namespace EcsServerLibM
{

	//public class ArcheTypeM
	//{
	//    public uint[] _bitId;
	//    public LinkedList<ChunkM> _chunkList = new LinkedList<ChunkM>();
	//    public ObjPoolM<ChunkM> _chunkPool = new ObjPoolM<ChunkM>();



	//    public ArcheTypeM(IEnumerable<TypeValueM> typeValueList, ref EntityM entity)
	//    {
	//        _bitId = entity._bitId;
	//        AddChunkAndSetData(typeValueList, ref entity);
	//    }

	//    void AddChunkAndSetData(IEnumerable<TypeValueM> typeValueList, ref EntityM entity)
	//    {
	//        var chunk = _chunkPool.Rent();  // 청크풀에서 얻어옴
	//        if (chunk.IsRecyleChunk() == false)// 재활용이면 만들 필요 없음 (clear는 리스트 _chunkPool로 옮길 때 함)
	//        {
	//            chunk.MakeAndSetData(typeValueList, ref entity);    // 재활용이 아니면 다시 만듬
	//        }

	//        entity._iChunkListIndex = _chunkList.Count();
	//        _chunkList.AddLast(chunk);
	//    }

	//    /// <summary>
	//    /// 청크 데이터 추가후 archeIndex와 ChunkIndex를 Entity에 설정해서 리턴
	//    /// </summary>
	//    /// <param name="typeValueList"></param>
	//    /// <param name="entity"></param>
	//    public void AddChunkData(IEnumerable<TypeValueM> typeValueList, ref EntityM entity)
	//    {
	//        bool isSet = false;
	//        int iChunkListIndex = 0;
	//        foreach(var chunk in _chunkList)
	//        {
	//            if(chunk.IsFullData() == false)
	//            {
	//                chunk.SetData(typeValueList, ref entity);
	//                entity._iChunkListIndex = iChunkListIndex;
	//                isSet = true;
	//                break;
	//            }
	//            iChunkListIndex++;
	//        }

	//        if(isSet == false)
	//        {
	//            AddChunkAndSetData(typeValueList, ref entity);
	//        }
	//    }

	//    public void SetChunkData(in EntityM entity, IEnumerable<TypeValueM> typeValueList)
	//    {
	//        var targetChunk = _chunkList.ElementAt(entity._iChunkListIndex);
	//        targetChunk.SetData(entity._iChunkIndex, typeValueList);
	//    }

	//    public void SetChunkData<T>(in EntityM entity, object value) where T : IComponentM
	//    {
	//        var targetChunk = _chunkList.ElementAt(entity._iChunkListIndex);
	//        targetChunk.SetData(entity._iChunkIndex, typeof(T), value);
	//    }

	//    public object GetChunkData<T>(in EntityM entity) where T : IComponentM
	//    {
	//        var targetChunk = _chunkList.ElementAt(entity._iChunkListIndex);
	//        return targetChunk.GetData<T>(entity._iChunkIndex);            
	//    }

	//    public void RemoveChunkData<T>(in EntityM entity) where T : IComponentM
	//    {
	//        var targetChunk = _chunkList.ElementAt(entity._iChunkListIndex);
	//        targetChunk.RemoveData<T>(entity._iChunkIndex);

	//        //if(targetChunk.Count() == 0  && _chunkList.Count() != 0)
	//        //{
	//        //    return false;
	//        //}

	//        //return true;
	//    }

	//    /// <summary>
	//    /// 청크 리스트에서 삭제 --> Pool에 리넡
	//    /// </summary>
	//    /// <param name="iChunkListIndex"></param>
	//    public void DestroyChunk(int iChunkListIndex)  // archeTypeId 를 가진 Entity 모두 조사해서 iChunkListIndex 모두 1개씩 갱신해야 됨
	//    {

	//    }       
	//}

	//public class ChunkM
	//{
	//    const int CHUNK_ARRAY_SIZE = 16000; // 16KB
	//    int _iMaxChunkIndex;
	//    int _iCurChunkIndex;

	//    public Dictionary<string, Array> _dicComponentArrays = new Dictionary<string, Array>();

	//    public bool IsFullData()
	//    {
	//        return (_iMaxChunkIndex == _iCurChunkIndex);
	//    }

	//    public int Count()
	//    {
	//        return _iCurChunkIndex;
	//    }

	//    public bool IsRecyleChunk()
	//    {
	//        if(_dicComponentArrays.Count() <= 0)
	//            return false;

	//        return true;
	//    }

	//    public void Clear()
	//    {
	//        foreach(var arr in _dicComponentArrays.Values)
	//        {
	//            Array.Clear(arr, 0, arr.Length);
	//        }
	//    }

	//    public void MakeAndSetData(IEnumerable<TypeValueM> typeValueList, ref EntityM entity)
	//    {

	//        if (typeValueList.Count() <= 0)
	//        {
	//            Debug.WriteLine("타입밸류 가 이상;;;");
	//            return;
	//        }


	//        /////////////////////////////////////////////////////////////////////////
	//        int totalTypeSize = 0;
	//        foreach (var typeValue in typeValueList)
	//        {
	//            totalTypeSize += System.Runtime.InteropServices.Marshal.SizeOf(typeValue._type);
	//        }
	//        _iMaxChunkIndex = CHUNK_ARRAY_SIZE / totalTypeSize;

	//        Type type;
	//        string strBitId;
	//        foreach (var typeValue in typeValueList)
	//        {
	//            type = typeValue._type;
	//            strBitId = typeValue._strBitId;
	//            Array arr = Array.CreateInstance(type, _iMaxChunkIndex);
	//            arr.SetValue(typeValue._value, _iCurChunkIndex);    // 값 설정
	//            _dicComponentArrays.Add(strBitId, arr);
	//        }

	//        entity._iChunkIndex= _iCurChunkIndex;

	//        _iCurChunkIndex++;  // 인덱스 하나 늘림
	//    }


	//    public void SetData(IEnumerable<TypeValueM> typeValueList, ref EntityM entity)
	//    {            
	//        if (typeValueList.Count() <= 0) // 예외 처리
	//        {
	//            Debug.WriteLine("타입밸류 가 이상---;;;");
	//            return;
	//        }

	//        // entity에 ChunkIndex값 채워넣기
	//        entity._iChunkIndex = _iCurChunkIndex;

	//        foreach (var typeValue in typeValueList)
	//        {
	//            var strBitId = typeValue._strBitId;
	//            if(_dicComponentArrays.TryGetValue(strBitId, out Array arr) == true)
	//            {
	//                arr.SetValue(typeValue._value, _iCurChunkIndex);
	//            }
	//            else
	//            {
	//                throw new Exception("컴포넌트 Id에 해당하는 Chunk Array가 없음");
	//            }
	//        }

	//        _iCurChunkIndex++;
	//    }

	//    public void SetData(int iChunkIndex, IEnumerable<TypeValueM> typeValueList)
	//    {
	//        foreach (var typeValue in typeValueList)
	//        {
	//            var strBitId = typeValue._strBitId;
	//            if (_dicComponentArrays.TryGetValue(strBitId, out Array arr) == true)
	//            {
	//                arr.SetValue(typeValue._value, iChunkIndex);
	//            }
	//            else
	//            {
	//                throw new Exception("컴포넌트 Id에 해당하는 Chunk Array가 없음");
	//            }
	//        }
	//    }

	//    public void SetData(int iChunkIndex, Type type, object value)
	//    {
	//        var strBitId = EcsSystemM.GetComponentStrBitId(type);
	//        if (_dicComponentArrays.TryGetValue(strBitId, out Array arr) == true)
	//        {
	//            arr.SetValue(value, iChunkIndex);
	//        }
	//        else
	//        {
	//            throw new Exception("컴포넌트 Id에 해당하는 Chunk Array가 없음용");
	//        }
	//    }

	//    public object GetData<T>(int iChunkIndex) where T : IComponentM
	//    {
	//        var strBitId = EcsSystemM.GetComponentStrBitId(typeof(T));
	//        if (_dicComponentArrays.TryGetValue(strBitId, out Array arr) == true)
	//        {
	//            return arr.GetValue(iChunkIndex);
	//        }
	//        else
	//        {
	//            throw new Exception("컴포넌트 Id에 해당하는 Chunk Array가 없음용-");
	//        }
	//    }


	//    public void RemoveData<T>(int iChunkIndex)
	//    {
	//        if(_iCurChunkIndex <= 0)    // 배열에 아이템이 없음
	//        {
	//            throw new ArgumentException("배열에 지울 아이템이 없음");
	//        }

	//        var strBitId = EcsSystemM.GetComponentStrBitId(typeof(T));
	//        if (_dicComponentArrays.TryGetValue(strBitId, out Array arr) == true)
	//        {
	//            Array.Clear(arr, iChunkIndex, 1);   // 현재 자리 지움
	//            if (iChunkIndex != _iCurChunkIndex - 1) // 중간이면 끌어 담김
	//            {
	//                arr.SetValue(arr.GetValue(_iCurChunkIndex - 1), iChunkIndex);
	//            }                 
	//        }
	//        else
	//        {
	//            throw new Exception("컴포넌트 Id에 해당하는 Chunk Array가 없음용--");
	//        }
	//        _iCurChunkIndex--;
	//    }


	//    //public bool SetEntityData(EntityM entity)
	//    //{
	//    //    if (_dicComponentArray.Count() <= 0)    // 새로 생성
	//    //    {
	//    //        var bitIdList = BitIdM.SplitBitIds(entity._bitId);
	//    //        var typeList = EcsSystemM.GetTypeListWithBitIds(bitIdList);
	//    //        var strIdList = BitIdM.ToStrBitIdsList(bitIdList);

	//    //        Init(strIdList, typeList);
	//    //    }

	//    //    if (_iCurChunkIndex >= _iMaxChunkIndex)
	//    //        return false;                       

	//    //    _dicComponentArray.TryGetValue("01", out Array arr);
	//    //    arr.SetValue()


	//    //}


	//}

	//public interface IComponentM
	//{

	//}

	//public struct ComponentM : IComponentM
	//{
	//    int x;
	//}


	//public class SystemM
	//{

	//}

	//public struct TypeValueM
	//{
	//    public Type _type;
	//    public object _value;
	//    public string _strBitId;
	//    public uint[] _bitId;

	//    public TypeValueM(Type type, object value)
	//    {
	//        _type = type;
	//        _value = value;
	//        _strBitId = string.Empty;
	//        _bitId = null;
	//    }

	//    public void SetStrBitId(string strBitId)
	//    {
	//        _strBitId = strBitId;
	//    }

	//    public void SetBitId(uint[] bitId)
	//    {
	//        _bitId = bitId;
	//    }


	//}

	//public static class EcsSystemM
	//{
	//    public static Dictionary<Type, string> gDicComponentMForType  = new Dictionary<Type, string>();
	//    public static Dictionary<string, Type> gDicComponentMForBitId = new Dictionary<string, Type>();

	//    static public List<Type> GetTypeListWithBitIds(IEnumerable<uint[]> bitIds)
	//    {
	//        var typeList = new List<Type>();
	//        string strBitId;
	//        foreach(var bitId in bitIds)
	//        {
	//            strBitId = BitIdM.ToStringBitId(bitId);
	//            if (gDicComponentMForBitId.TryGetValue(strBitId, out Type type) == true)
	//                typeList.Add(type);
	//            else
	//                throw new ArgumentException("콤포넌트에 등록되지 않은 bitId를 조회함");
	//        }

	//        return typeList;
	//    }

	//    static public List<string> GetStrBitIdListWithTypeList(IEnumerable<Type> typeList)
	//    {
	//        List<string> strBitIdList = new List<string>();
	//        string strBitId;
	//        foreach(var type in typeList)
	//        {
	//            strBitId = GetComponentStrBitId(type);
	//            strBitIdList.Add(strBitId);
	//        }
	//        return strBitIdList;
	//    }

	//    static public bool IsRegisteredComponentType(Type type)
	//    {
	//        return gDicComponentMForType.ContainsKey(type);
	//    }

	//    static public bool IsRegisteredComponentMType(string strBitId)
	//    {
	//        return gDicComponentMForBitId.ContainsKey(strBitId);
	//    }

	//    static public string GetComponentStrBitId(Type type)
	//    {
	//        gDicComponentMForType.TryGetValue(type, out string strBitId);
	//        return strBitId;
	//    }

	//    static public Type GetComponentMType(string strBitId)
	//    {
	//        gDicComponentMForBitId.TryGetValue(strBitId, out Type type);
	//        return type;
	//    }

	//    static public void RegisterComponentMType(string strBitId, Type type)
	//    {
	//        gDicComponentMForBitId.Add(strBitId, type);
	//        gDicComponentMForType.Add(type, strBitId);
	//    }

	//    static public void RegisterComponentType(Type type, string strBitId)
	//    {
	//        gDicComponentMForType.Add(type, strBitId);
	//        gDicComponentMForBitId.Add(strBitId, type);            
	//    }

	//    ///////////////////////////////////////////////////////////////////////////////
	//    /// 아키 타입 관련
	//    /// 

	//    public static Dictionary<string, ArcheTypeM> gDicArchyTypeM = new Dictionary<string, ArcheTypeM>();

	//    static public bool IsRegisteredArchyType(string strArchTypeId)
	//    {
	//        return gDicArchyTypeM.ContainsKey(strArchTypeId);
	//    }

	//}    




	///// <summary>
	///// strBitId 저장을 bit로 하는데 uint [0] 배열이 1 ~ 32개의 index이다
	///// </summary>
	//static public class EntityManagerM
	//{
	//    static int iCntComponenMId;


	//    /// <summary>
	//    /// Entity의 iArcheIndex와 iChunckIndex를 결정해서 채워 넣고 Data를 청크에 넣기
	//    /// </summary>
	//    /// <param name="strArcheTypeId"></param>
	//    /// <param name="typeValueList"></param>
	//    static public void GetEntityData(string strArcheTypeId, IEnumerable<TypeValueM> typeValueList, ref EntityM entity)
	//    {
	//        if (EcsSystemM.IsRegisteredArchyType(strArcheTypeId) == false)  // 없으면 생성 등록
	//        {
	//            var archeType = new ArcheTypeM(typeValueList, ref entity);
	//            EcsSystemM.gDicArchyTypeM.Add(strArcheTypeId, archeType);
	//        }
	//        else
	//        {
	//            if (EcsSystemM.gDicArchyTypeM.TryGetValue(strArcheTypeId, out ArcheTypeM archeType) == true)
	//            {
	//                archeType.AddChunkData(typeValueList, ref entity);
	//            }
	//        }
	//    }

	//    /// <summary>
	//    /// 이미 생성된 Entity를 가지고 값들 변경
	//    /// </summary>
	//    /// <param name="entity"></param>
	//    /// <param name="typeValueList"></param>
	//    static public void SetEntityData(in EntityM entity, IEnumerable<TypeValueM> typeValueList)
	//    {
	//        if (EcsSystemM.gDicArchyTypeM.TryGetValue(entity._stringBitId, out ArcheTypeM archeType) == true)
	//        {
	//            archeType.SetChunkData(entity, typeValueList);
	//        }
	//    }

	//    /// <summary>
	//    /// Entity를 가지고 값 변경
	//    /// </summary>
	//    /// <param name="entity"></param>
	//    /// <param name="type"></param>
	//    /// <param name="value"></param>
	//    static public void SetEntityData<T>(in EntityM entity, T value) where T : IComponentM
	//    {
	//        if (EcsSystemM.gDicArchyTypeM.TryGetValue(entity._stringBitId, out ArcheTypeM archeType) == true)
	//        {
	//            archeType.SetChunkData<T>(in entity, value);
	//        }
	//    }

	//    /// <summary>
	//    /// Entity에 컴포넌트 추가
	//    /// </summary>
	//    /// <typeparam name="T"></typeparam>
	//    /// <param name="entity"></param>
	//    /// <param name="value"></param>
	//    //static public void AddComponent<T>(ref EntityM entity, T value) where T : IComponentM
	//    //{
	//    //    if (EcsSystemM.gDicArchyTypeM.TryGetValue(entity._stringBitId, out ArcheTypeM archeType) == false)
	//    //}


	//    /// <summary>
	//    /// EntityMaker 생성
	//    /// </summary>
	//    /// <returns></returns>
	//    public static EntityMakerM CreateEntityMakerM()
	//    {
	//        return EntityMakerM.CreateEntityMakerM();
	//    }


	//    // 컴포넌트 하나로 바로 Entity 생성
	//    public static EntityM Initiate<T>(T vaule) where T : IComponentM
	//    {
	//        var entityMaker = CreateEntityMakerM();
	//        var entity = entityMaker.AddComponent(vaule).MakeEntity();
	//        return entity;
	//    }

	//    public class TypeValueMakerM
	//    {
	//        List<TypeValueM> _typeValueList = new List<TypeValueM>();
	//        public TypeValueMakerM AddComponent<T>(T value) where T : IComponentM
	//        {
	//            _typeValueList.Add(new TypeValueM(typeof(T), value));
	//            return this;
	//        }

	//        public List<TypeValueM> MakeTypeValueList()
	//        {
	//            return _typeValueList;
	//        }

	//        static public TypeValueMakerM CreateTypeValueMaker()
	//        {
	//            return new TypeValueMakerM();
	//        }

	//        private TypeValueMakerM() { }
	//    }

	//    /// <summary>
	//    /// TypeValue만들기
	//    /// </summary>
	//    /// <typeparam name="T"></typeparam>
	//    /// <param name="value"></param>
	//    /// <returns></returns>
	//    public static TypeValueM CreateTypeValueM<T>(T value) where T : IComponentM
	//    {
	//        return new TypeValueM(typeof(T), value);
	//    }

	//    /// <summary>
	//    /// 여러 컴포넌트를 추가해서 Entity를 만드는 도우미 클래스
	//    /// </summary>
	//    public class EntityMakerM
	//    {
	//       TypeValueMakerM _typeValueMaker = TypeValueMakerM.CreateTypeValueMaker();   

	//        public EntityMakerM AddComponent<T>(T value) where T : IComponentM
	//        {
	//           _typeValueMaker.AddComponent(value);                      
	//            return this;
	//        }

	//        //public EntityMakerM AddComponent(Type type, object value)
	//        //{
	//        //    _typeValueList.Add(new TypeValueM(type, value));
	//        //    return this;
	//        //}

	//        //public EntityMakerM AddComponent(TypeValueM typeValue)
	//        //{
	//        //    _typeValueList.Add(typeValue);
	//        //    return this;
	//        //}            

	//        private EntityMakerM() { }

	//        public static EntityMakerM CreateEntityMakerM()
	//        {
	//            return new EntityMakerM();
	//        }

	//        public EntityM MakeEntity()
	//        {
	//            List<TypeValueM> typeValueList = _typeValueMaker.MakeTypeValueList();

	//            // Componunt들의 id들을 조회하고 없으며 컴포넌트 관리자에게 등록 - id발행
	//            // 모든 컴포넌트 리스트들의 id를 조합해서 archyIndex의 EntityM을 만듬
	//            uint[] archeTypeId = new uint[0];
	//            uint[] bitId;

	//            for (int i = 0; i < typeValueList.Count(); i++)
	//            {
	//                TypeValueM typeValue = typeValueList[i];

	//                if (EcsSystemM.IsRegisteredComponentType(typeValue._type) == false)
	//                {
	//                    bitId = BitIdM.CreateNewBitId(ref iCntComponenMId);                     //컴포넌트 아이디 발급  
	//                    EcsSystemM.RegisterComponentType(typeValue._type, BitIdM.ToStringBitId(bitId)); // 컴포넌트 등록                        
	//                }
	//                else
	//                {
	//                    var strBitId = EcsSystemM.GetComponentStrBitId(typeValue._type);
	//                    bitId = BitIdM.ToBitId(strBitId);
	//                }

	//                typeValue.SetBitId(bitId);  // bitId
	//                typeValue.SetStrBitId(BitIdM.ToStringBitId(bitId)); //strBitId
	//                archeTypeId = BitIdM.Add(archeTypeId, bitId);

	//                typeValueList[i] = typeValue;
	//            }

	//            var strArcheTypeId = BitIdM.ToStringBitId(archeTypeId);

	//            EntityM entity = new EntityM(archeTypeId, strArcheTypeId);
	//            GetEntityData(strArcheTypeId, typeValueList, ref entity);                                

	//            return entity;
	//        }
	//    }

	//}

	//public struct EntityM
	//{
	//    public uint[] _bitId;
	//    public string _stringBitId;
	//    public int _iChunkIndex;
	//    public int _iChunkListIndex;


	//    public void PrintIdDebug()
	//    {
	//        BitIdM.PrintBitId(_bitId);
	//    }

	//    public EntityM(uint[] bitId, string stringBitId = null)
	//    {
	//        if(bitId == null)
	//            _bitId = new uint[0];
	//        else
	//            _bitId = bitId;

	//        _iChunkIndex = 0;
	//        _iChunkListIndex = 0;

	//        if(stringBitId != null)
	//            _stringBitId = stringBitId; 
	//        else
	//            _stringBitId = BitIdM.ToStringBitId(bitId);
	//    }


	//    static public EntityM operator |(EntityM a, EntityM b)
	//    {
	//        var rtn = BitIdM.Add(a._bitId, b._bitId);
	//        return new EntityM(rtn);
	//    }

	//    static public EntityM operator &(EntityM a, EntityM b)
	//    {
	//        var rtn = BitIdM.InterSect(a._bitId, b._bitId);
	//        return new EntityM(rtn);
	//    }

	//    static public EntityM operator +(EntityM a, EntityM b)
	//    {
	//        return (a | b);
	//    }

	//    static public EntityM operator -(EntityM a, EntityM b)
	//    {
	//        var rtn = BitIdM.Sub(a._bitId, b._bitId);
	//        return new EntityM(rtn);
	//    }

	//}

}
