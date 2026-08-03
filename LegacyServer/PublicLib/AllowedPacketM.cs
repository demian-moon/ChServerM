using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace EcsServerLibM
{
	/* 사용 예 */

	//AllowedPacketMan.AllowedPacketManBuilder apmb = new AllowedPacketMan.AllowedPacketManBuilder();

	//// 스타트 패킷 스테이트 추가
	//apmb.StartAllowedPkGroup(ALLOWED_PACKET_STATE.START);
	//apmb.AddPacketType(PACKET_TYPE.LOGIN);
	//apmb.EndAllowedPkGroup();

	//apmb.StartAllowedPkGroup(ALLOWED_PACKET_STATE.ANY_STATE);
	//apmb.AddPacketGroup(ALLOWED_PACKET_STATE.START);            // 위에서 등록한 ALLOWED_PACKET_STATE.START 그룹 등록
	//apmb.AddPacketGroup((ALLOWED_PACKET_STATE) SERVER_ALLOWED_PACKET_STATE.ROBBY);   // AllowedPacketMan에 등록한 STATE만 등록 가능 (오류-메세지 출력)
	//apmb.AddPacketType(PACKET_TYPE.LOGOUT);
	//apmb.EndAllowedPkGroup();

	//AllowedPacketMan pkMan = apmb.Build();

	//var bOk = pkMan.IsAllowed(ALLOWED_PACKET_STATE.ANY_STATE, PACKET_TYPE.LOGIN);
	//Debug.WriteLine("처리해도 되는 패킷?:" + bOk);       // 리턴 TRUE



	public enum ALLOWED_PACKET_STATE
	{
		// 서버클라, 공용 스테이트
		A_SC_NOT_LOGINED = 30000,
		A_SC_ANY_STATE, // 어떤 패킷이든 상관없이 다 받는 스테이트 - 보통 클라에서 편하게 세팅
		A_SC_START,     // 제일 처음 스테이트 - VERSION_CHECK 밖에 처리 하지 않음        

		// 서버 스테이트


		// 클라 스테이트

	}

	// 기본 아이디어
	// 패킷 타입의 트리 구조:
	// composite 패턴을 통해서 IAllowedPacket 인터페이스를 둘다 상속받게 하고
	// AllowedPacketItem(pkType) - 아이템 IAllowedPacket를 상속 받음
	// AllowedPacketGroup이 IAllowedPacket를 상속받고 멤버로 IAllowedPacket리스트를 갖는 트리 구조

	// AllowedPacketMan : 처리할 수 있는 패킷인지 확인을 위해서 위 트리구조를 이용하는 매니저 (빌더를 통해서만 만듬)
	// 기본적으로 ALLOWED_PACKET_STATE, AllowedPacketGroup을 dictionanry 멤버로
	// _pkAllAlloweded 라는 이름으로 모든 스테이트에서 받아주는 패킷을 리스트로 관리
	// 참고로 : ALLOWED_PAKET_STATE.ANY는 모든 그룹에서 받아주는 패킷으로 처리 됨

	// 빌더 패턴 (AllowedPacketManBuilder)
	// StartAllowedPkGroup(ALLOWED_PACKET_STATE) ~ EndAllowedPkGroup(ALLOWED_PACKET_STATE)을 통해서 패킷 그룹 등록
	// Build()를 통해서 최종 AllowedPacketMan 만들어냄 

	public interface IAllowedPacket
	{
		bool IsAllowedPacket(PACKET_TYPE curPkType);

	}


	public class AllowedPacketItem : IAllowedPacket
	{
		PACKET_TYPE _pkType;

		public AllowedPacketItem(PACKET_TYPE pkType)
		{
			_pkType = pkType;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsAllowedPacket(PACKET_TYPE curPkType)
		{
			if (_pkType != curPkType)
				return false;

			return true;
		}

	}

	public class AllowedPacketGroup : IAllowedPacket
	{
		private ALLOWED_PACKET_STATE _allowedPkState;
		protected List<IAllowedPacket> _allowedPkList = new List<IAllowedPacket>();   // 받을 수 있는 패킷 타입 


		public AllowedPacketGroup(ALLOWED_PACKET_STATE allowedState)
		{
			AllowedPkState = allowedState;
		}

		public AllowedPacketGroup(ALLOWED_PACKET_STATE allowedState, IAllowedPacket allowedPacketInterface)
		{
			AllowedPkState = allowedState;
			_allowedPkList.Add(allowedPacketInterface);
		}

		public ALLOWED_PACKET_STATE AllowedPkState { get => _allowedPkState; set => _allowedPkState = value; }

		public void Add(IAllowedPacket allowedPacketInterface)    // 패킷 타입 추가
		{
			_allowedPkList.Add(allowedPacketInterface);
		}

		public void Add(PACKET_TYPE pkType)
		{
			AllowedPacketItem allowedPacketItem = new AllowedPacketItem(pkType);
			Add(allowedPacketItem);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsAllowedPacket(PACKET_TYPE curPkType)
		{
			foreach (IAllowedPacket allowedPk in _allowedPkList)
			{
				if (allowedPk.IsAllowedPacket(curPkType) == true)
					return true;
			}

			return false;
		}

		public int Count()
		{
			return _allowedPkList.Count;
		}
	}

	// 
	/// <summary>
	/// 처리 할 수 있는 패킷인지 검사하는 패킷 매니저 
	/// </summary>
	public class AllowedPacketMan
	{
		Dictionary<ALLOWED_PACKET_STATE, AllowedPacketGroup> _dicPacketMan = new Dictionary<ALLOWED_PACKET_STATE, AllowedPacketGroup>();
		List<PACKET_TYPE> _pkAllAlloweded = new List<PACKET_TYPE>();


		void AddPkGroup(AllowedPacketGroup allowedPacketGroup)
		{
			if (allowedPacketGroup.Count() > 0)
				_dicPacketMan.Add(allowedPacketGroup.AllowedPkState, allowedPacketGroup);

		}

		void SetPkAllAllowed(List<PACKET_TYPE> pkListAllGroupAllowed)
		{
			_pkAllAlloweded = pkListAllGroupAllowed;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsAllowed(ALLOWED_PACKET_STATE curPkState, PACKET_TYPE curPkType)
		{
			// 패킷 스테이트가 A_SC_ANY_STATE 어떤 패킷이든 다 받는 스테이트라면 무조건 true
			if (curPkState == ALLOWED_PACKET_STATE.A_SC_ANY_STATE)
				return true;

			if (_pkAllAlloweded.IndexOf(curPkType) >= 0)  // 모든 스테이트에서 받아주는 패킷이라면 
			{
				return true;
			}

			AllowedPacketGroup allowedPacketGroup;
			if (_dicPacketMan.TryGetValue(curPkState, out allowedPacketGroup) == true)
			{
				return allowedPacketGroup.IsAllowedPacket(curPkType);
			}
			else
			{
				return false;
			}
		}

		// Dictionary에 등록된 패킷 그룹 얻기
		public AllowedPacketGroup GetAllowedPacketGroup(ALLOWED_PACKET_STATE allowedPkState)
		{
			AllowedPacketGroup allowedPacketGroup;
			_dicPacketMan.TryGetValue(allowedPkState, out allowedPacketGroup);

			return allowedPacketGroup;
		}

		// 오직 AllowedPacketManBuilder를 통해서만 생성
		private AllowedPacketMan() { }

		/// <summary>
		/// 패킷 매니저를 만드는 Builder
		/// </summary>
		public class AllowedPacketManBuilder
		{
			AllowedPacketGroup _allowedPacketGroup;
			AllowedPacketMan _allowedPacketMan = new AllowedPacketMan();

			List<PACKET_TYPE> _pkListAllAllowed = new List<PACKET_TYPE>();  // 모든 그룹에서 받아줄 패킷들

			/// <summary>
			/// ALLOWED_PACKET_STATE 값에 매칭되는 패킷 or 패킷그룹을 만들기 위해 처음 호출하는 함수
			/// 마지막은 EndAllowedPkGroup
			/// </summary>
			/// <param name="allowedPkState">이 스테이트일때 받아주는 패킷(or패킷그룹)을 찾는 키값으로 쓰임</param>            
			public void StartAllowedPkGroup(ALLOWED_PACKET_STATE allowedPkState)
			{
				var pg = _allowedPacketMan.GetAllowedPacketGroup(allowedPkState);

				if (pg != null)
				{
					throw new Exception("등록된 패킷 그룹이 이미 있습니다. 새로운 패킷그룹만 등록하실 수 있어요");
				}

				if (_allowedPacketGroup != null)
				{
					throw new Exception("진행중이던 패킷그룹 등록이 있습니다. 패킷그룹 등록을 완료한 후에 추가해주세요!!!");
				}
				_allowedPacketGroup = new AllowedPacketGroup(allowedPkState);
			}

			public void AddPacketType(PACKET_TYPE pkType)
			{
				if (_allowedPacketGroup is null)
				{
					throw new Exception("StartAllowedPkGroup 함수로 등록을 시작하세요");
				}

				_allowedPacketGroup.Add(pkType);
			}


			/// <summary>
			/// 이미 만든 패킷그룹을 ALLOWED_PACKET_STATE를 키로, 찾아서 추가한다.
			/// </summary>
			/// <param name="allowedPkState"></param>
			public void AddAlreadResteredPkGroup(ALLOWED_PACKET_STATE allowedPkState)
			{
				if (_allowedPacketGroup is null)
				{
					throw new Exception("StartAllowedPkGroup 함수로 등록을 시작하세요");
				}

				var pg = _allowedPacketMan.GetAllowedPacketGroup(allowedPkState);

				if (pg != null)
				{
					_allowedPacketGroup?.Add(pg);
				}
				else
				{
					throw new Exception("_dicPacketMan에 등록되어 있지 않은 그룹입니다.");
				}
			}


			/// <summary>
			/// 모든  ALLOWED_PACKET_STATE에서 허용 되는 패킷 타입추가 한다.
			/// ex) HeartBit 라던가
			/// </summary>
			/// <param name="pkType"></param>
			public void AddPacketAllAllowed(PACKET_TYPE pkType)
			{
				_pkListAllAllowed.Add(pkType);
			}

			/// <summary>
			/// 패킷 그룹을 만드는 StartAllowedPkGroup호출후 이 함수를 호출하면 패킷 그룹이 만들어 진다.
			/// </summary>
			public void EndAllowedPkGroup()
			{
				if (_allowedPacketGroup.Count() > 0)
					_allowedPacketMan.AddPkGroup(_allowedPacketGroup);
				_allowedPacketGroup = null;
			}

			/// <summary>
			/// 최종적으로 호출해야 되는 함수
			/// </summary>
			/// <returns></returns>
			/// <exception cref="NotImplementedException"></exception>
			public AllowedPacketMan Build()
			{
				if (_allowedPacketGroup != null)
				{
					Debug.WriteLine("진행중이던 패킷그룹 등록이 있습니다. 패킷그룹 등록을 완료해주세요!!!");
					throw new NotImplementedException("진행중이던 패킷그룹 등록이 있습니다. 패킷그룹 등록을 완료해주세요!!!");
				}

				if (_allowedPacketMan._dicPacketMan.Count <= 0)
				{
					throw new NotImplementedException("등록된 ALLOWED_PACKET_STATE의 패킷그룹 개수가 0입니다");
				}

				_allowedPacketMan.SetPkAllAllowed(_pkListAllAllowed);  // 모든 그룹에서 허용하는 패킷들 등록
				return _allowedPacketMan;
			}
		}
	}




	//////////// 코드 백업 ////////////////

	//public abstract class AbAllowedPacketM
	//{
	//    private ALLOWED_PACKET_STATE _allowedPkState;


	//    public ALLOWED_PACKET_STATE AllowedPkState { get => _allowedPkState; set => _allowedPkState = value; }


	//    abstract public bool IsAllowedPacket(ALLOWED_PACKET_STATE curAllowedState, PACKET_TYPE pkType, bool bFindAllowedState);

	//    abstract public AbAllowedPacketM GetAllowedPkState(ALLOWED_PACKET_STATE curAllowedState);

	//}

	//public class AllowedPacketItem : AbAllowedPacketM
	//{
	//    protected SortedList<PACKET_TYPE, PACKET_TYPE> _pkTypeList = new SortedList<PACKET_TYPE, PACKET_TYPE>();   // 받을 수 있는 패킷 타입 


	//    public void AddPacketType(PACKET_TYPE ePacketType)    // 패킷 타입 추가
	//    {
	//        _pkTypeList.Add(ePacketType, ePacketType);
	//    }

	//    public AllowedPacketItem(ALLOWED_PACKET_STATE allowedState, PACKET_TYPE ePacketType)
	//    {
	//        AllowedPkState = allowedState;
	//        _pkTypeList.Add(ePacketType, ePacketType);
	//    }

	//    public AllowedPacketItem(ALLOWED_PACKET_STATE allowedState)
	//    {
	//        AllowedPkState = allowedState;
	//    }

	//    public override AbAllowedPacketM GetAllowedPkState(ALLOWED_PACKET_STATE curAllowedState)
	//    {
	//        if (AllowedPkState == curAllowedState)
	//            return this;

	//        return null;
	//    }

	//    public override bool IsAllowedPacket(ALLOWED_PACKET_STATE curAllowedState, PACKET_TYPE pkType, bool bFindAllowedState)
	//    {
	//        if (curAllowedState == ALLOWED_PACKET_STATE.ANY_STATE)
	//            return true;

	//        if (bFindAllowedState == false) // 이미 스테이트를 찾은게 아니면 state와 pkType 둘다 비교
	//        {
	//            if (AllowedPkState != curAllowedState)
	//                return false;
	//        }

	//        if (_pkTypeList.IndexOfKey(pkType) >= 0)
	//            return true;

	//        return false; // pkType 다름
	//    }


	//}

	//public class AllowedPacketGroup : AbAllowedPacketM
	//{
	//    SortedList<ALLOWED_PACKET_STATE, AbAllowedPacketM> _allowedPkList = new SortedList<ALLOWED_PACKET_STATE, AbAllowedPacketM>();


	//    public AllowedPacketGroup(ALLOWED_PACKET_STATE allowedState)
	//    {
	//        AllowedPkState = allowedState;
	//    }


	//    public void Add(AbAllowedPacketM allowedPackets)
	//    {
	//        _allowedPkList.Add(allowedPackets.AllowedPkState, allowedPackets);
	//    }


	//    public override AbAllowedPacketM GetAllowedPkState(ALLOWED_PACKET_STATE curAllowedState)
	//    {
	//        if (AllowedPkState == curAllowedState)
	//            return this;

	//        foreach (AbAllowedPacketM allowdPk in _allowedPkList.Values)
	//        {
	//            var tmAllowedPk = allowdPk.GetAllowedPkState(curAllowedState);
	//            if (tmAllowedPk != null)
	//                return tmAllowedPk;
	//        }

	//        return null;
	//    }

	//    public override bool IsAllowedPacket(ALLOWED_PACKET_STATE curAllowedState, PACKET_TYPE pkType, bool bFindState = false)
	//    {
	//        if (curAllowedState == ALLOWED_PACKET_STATE.ANY_STATE)
	//            return true;

	//        if (bFindState == false)
	//        {
	//            if (AllowedPkState == curAllowedState || _allowedPkList.IndexOfKey(curAllowedState) >= 0)
	//                bFindState = true;
	//        }

	//        foreach (AbAllowedPacketM allowedPk in _allowedPkList.Values)
	//        {

	//            if (allowedPk.IsAllowedPacket(curAllowedState, pkType, bFindState) == true)
	//            {
	//                return true;
	//            }
	//        }

	//        return false;
	//    }

	//}

}
