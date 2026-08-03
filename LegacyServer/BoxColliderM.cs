using Arch.Core;
using Arch.Core.Extensions;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace EcsServerLibM
{

	public interface IColliderM
	{
		AbScriptableForGameObjM Owner { get; }
		IShapeM Bounds { get; }
		bool IsTrigger { get; set; }
		
		public long OnStayEventDelayTick { get; set; }
	}

	public struct QuadPointColliderM : IColliderM
	{
		ICollisionEventM Script
		{
			get
			{
				ref var scrtM = ref _owner.entity.Get<ObjScriptM>();
				return scrtM.script;
			}
		}

		AbScriptableForGameObjM _owner;
		public AbScriptableForGameObjM Owner => _owner;

		// 충돌 체크를 활성화 또는 비활성화하는 프로퍼티
		public bool _enabled { get; set; }

		// true이면 Collision 이벤트를 발생 시키지 않음, 대신 Trigger 이벤트를 발생시킴
		public bool IsTrigger { get; set; }

		static long STAY_EVENT_DELAY_TICK = (long)(Stopwatch.Frequency / 10); // OnStay 이벤트를 발생시키기 위한 딜레이 틱 수 (0.1초)
		public long OnStayEventDelayTick { get; set; } = STAY_EVENT_DELAY_TICK; // OnStay 이벤트를 발생시키기 위한 딜레이 틱 수
		long _lastStayEventTick; // OnStay 이벤트가 발생한 마지막 틱

		HashSet<Entity> _curCollisionObjs = new HashSet<Entity>();

		HashSet<Entity> _lastCollisionObjs = new HashSet<Entity>();

		public QuadPointBoundM cachedBounds;
		private PositionM cachedPosForCollider;



		public QuadPointColliderM(AbScriptableForGameObjM owner, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4)
		{
			_owner = owner;
			cachedBounds = new QuadPointBoundM(p1, p2, p3, p4);
			cachedPosForCollider = new PositionM();

		}

		// 사각형 일 때
		public QuadPointColliderM(AbScriptableForGameObjM owner, Vector3 size)   // 0, 0에 사이만큼으로 정사각형 생성
		{
			_owner = owner;

			var halfSizeX = size.X / 2;
			var halfSizeY = size.Y / 2;
			Vector3 p1 = new Vector3(-halfSizeX, -halfSizeY, 0);    // 좌하단
			Vector3 p2 = new Vector3(-halfSizeX, halfSizeY, 0);    // 좌상단
			Vector3 p3 = new Vector3(halfSizeX, halfSizeY, 0);    // 우상단
			Vector3 p4 = new Vector3(halfSizeX, -halfSizeY, 0);    // 우하단

			cachedBounds = new QuadPointBoundM(p1, p2, p3, p4);
			cachedPosForCollider = new PositionM();

		}

		public float AngleDegree { get; set; } = 0f; // 회전 각도 (도 단위)


		public void Rotate(float angleDegree)
		{
			((QuadPointBoundM)Bounds).Rotate(angleDegree); // Bounds는 IShapeM 인터페이스를 구현한 QuadPointBoundM 타입이므로 캐스팅하여 사용
			AngleDegree = MathM.NormalizeAngle(AngleDegree + angleDegree); // 회전 각도 업데이트			
		}

		public IShapeM Bounds
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get
			{
				var ownerPos = _owner.GetPos();
				if (cachedPosForCollider != ownerPos)
				{
					cachedBounds.ChangeCenter(ownerPos);
					cachedPosForCollider = ownerPos;
				}

				return cachedBounds;
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsCollision(IColliderM ohter)
		{
			return Bounds.Intersects(ohter.Bounds);
		}



		int CheckCollisionEventInHashSet(IColliderM collider, long curTick, long elapsedTick)
		{
			if (IsTrigger)
			{
				if (collider.IsTrigger == false) // 트리거가 아닌 콜라이더와 충돌했을 때 만 Stay 이벤트를 발생시킴 (유저끼리는 발생 안시킴)
				{					
					if(curTick - _lastStayEventTick < OnStayEventDelayTick) // 딜레이가 지나지 않았다면
						return 3; // Stay 이벤트를 발생시키지 않음

					OnTriggerStay(collider, curTick, elapsedTick);
					_lastStayEventTick = curTick;
				}
				return 3;
			}
			else
			{
				OnCollisionStay(new CollisionM(this, collider), curTick, elapsedTick);
				return 4;
			}
		}


		// 충돌없음 : 0
		// onTrigerEnter : 1
		// OnCollisionEnter : 2
		// OnTriggerStay : 3
		// onCollisionStay : 4
		// OnTriggerExit : 5
		// OnCollisionExit : 6        
		public int GenCollisionEvent(in Entity objEntity, long curTick, long elapsedTick)
		{
			ref var collider = ref objEntity.Get<QuadPointColliderM>();

			if (IsCollision(collider) == false)
				return 0;

			if (_lastCollisionObjs.Contains(objEntity) == false) // 새롭게 충돌여부 검사
			{
				if (IsTrigger)
				{
					OnTriggerEnter(collider, curTick, elapsedTick);
					_curCollisionObjs.Add(objEntity);
					return 1;
				}
				else
				{
					OnCollisionEnter(new CollisionM(this, collider), curTick, elapsedTick); ;
					_curCollisionObjs.Add(objEntity);
					return 2;
				}
			}
			else
			{
				return CheckCollisionEventInHashSet(collider, curTick, elapsedTick); // 이미 충돌해 있는 녀석이니까 여기서 검사
			}

			return 0;
		}


		public void CollisionEventGenerate(IEnumerable<Entity> entityList, long curTick, long elapsedTick)
		{
			foreach (var objEntity in entityList)
			{
				ref var collider = ref objEntity.Get<QuadPointColliderM>();

				_curCollisionObjs.Add(objEntity);

				if (_lastCollisionObjs.Contains(objEntity) == false) // 기존 충돌하지 않은 거면
				{
					if (IsTrigger)
					{
						OnTriggerEnter(collider, curTick, elapsedTick);
					}
					else
					{
						OnCollisionEnter(new CollisionM(this, collider), curTick, elapsedTick); ;
					}
				}
				else
				{
					CheckCollisionEventInHashSet(collider, curTick, elapsedTick); // 이미 충돌해 있는 녀석이니까 여기서 검사
				}
			}

			foreach (var entityObj in _lastCollisionObjs) // 충돌했다가 벗어난 것들 
			{
				if (entityObj.IsAlive() == false)   // 레퍼런스 하고 있으나 이미 Destory 됐을 수 있음
					continue;

				ref var collider = ref entityObj.Get<QuadPointColliderM>();

				if (_curCollisionObjs.Contains(entityObj) == false)
				{
					if (IsTrigger)
					{
						OnTriggerExit(collider, curTick, elapsedTick);
					}
					else
					{
						OnCollisionExit(new CollisionM(this, collider), curTick, elapsedTick);
					}
				}
			}

			_lastCollisionObjs = _curCollisionObjs;
			_curCollisionObjs = new HashSet<Entity>();

		}


		// 충돌체가 다른 객체와 충돌했을 때 호출되는 메서드

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void OnCollisionEnter(CollisionM collision, long curTick, long elapsedTick)
		{
			Script.OnCollisionEnter(collision, curTick, elapsedTick);
		}


		// 충돌체가 다른 객체와 충돌중일 때 호출되는 메서드
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void OnCollisionStay(CollisionM collision, long curTick, long elapsedTick)
		{
			Script.OnCollisionStay(collision, curTick, elapsedTick);
		}


		// 충돌체가 다른 객체와 충돌이 끝났을 때 호출되는 메서드
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void OnCollisionExit(CollisionM collision, long curTick, long elapsedTick)
		{
			Script.OnCollisionExit(collision, curTick, elapsedTick);
		}

		// 충돌체가 트리거 영역에 진입했을 때 호출되는 메서드
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void OnTriggerEnter(IColliderM other, long curTick, long elapsedTick)
		{
			Script.OnTriggerEnter(other, curTick, elapsedTick);
			Script.SendTriggerEnterPacketToUsers(other, curTick, elapsedTick);
		}

		// 충돌체가 트리거 영역에 머무르고 있을 때 호출되는 메서드
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void OnTriggerStay(IColliderM other, long curTick, long elapsedTick)
		{
			Script.OnTriggerStay(other, curTick, elapsedTick);
			Script.SendTriggerStayPacketToUsers(other, curTick, elapsedTick);
		}

		// 충돌체가 트리거 영역에서 벗어났을 때 호출되는 메서드
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void OnTriggerExit(IColliderM other, long curTick, long elapsedTick)
		{
			Script.OnTriggerExit(other, curTick, elapsedTick);
		}
	}


	public struct BoxColliderM : IColliderM
	{
		public ICollisionEventM Script
		{
			get
			{
				ref var scrtM = ref _owner.entity.Get<ObjScriptM>();
				return scrtM.script;
			}
		}
		AbScriptableForGameObjM _owner;
		public AbScriptableForGameObjM Owner => _owner;

		// 충돌 체크를 활성화 또는 비활성화하는 프로퍼티
		public bool _enabled { get; set; }

		// true이면 Collision 이벤트를 발생 시키지 않음, 대신 Trigger 이벤트를 발생시킴
		public bool IsTrigger { get; set; }
		static long STAY_EVENT_DELAY_TICK = (long)(Stopwatch.Frequency / 10); // OnStay 이벤트를 발생시키기 위한 딜레이 틱 수 (0.1초)
		public long OnStayEventDelayTick { get; set; } = STAY_EVENT_DELAY_TICK; // OnStay 이벤트를 발생시키기 위한 딜레이 틱 수
		long _lastStayEventTick; // OnStay 이벤트가 발생한 마지막 틱

		HashSet<Entity> _curCollisionObjs = new HashSet<Entity>();

		HashSet<Entity> _lastCollisionObjs = new HashSet<Entity>();

		public BoxColliderM(AbScriptableForGameObjM owner, Vector3 center = default, Vector3 size = default)
		{
			_owner = owner;
			if (center == default)
				Center = Vector3.Zero;
			else
				Center = center;

			if (size == default)
				Size = Vector3.One;
			else
				Size = size;
		}


		public Vector3 Center { get; set; } // 로컬 좌표 - Bounds는 계산에 쓰이는 월드좌표 데이터 구조체, 실제 콜라이더 상대적 위치는 이것임 (pixcel 단위 offset)
		public Vector3 Size { get; set; }   // pixel 사이즈 - 실제 콜라이더 사이즈는 이것임

		public RectM? cachedBounds;
		private PositionM cachedPosForCollider;

		// 충돌체의 Bounding Box 정보를 가져오는 프로퍼티


		public IShapeM Bounds
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get
			{
				var ownerPos = _owner.GetPos();
				if (cachedBounds == null)
				{
					cachedBounds = new RectM(ownerPos + Center, Size.X, Size.Y);
					cachedPosForCollider = ownerPos;
				}
				else if (cachedPosForCollider != ownerPos)
				{
					cachedBounds.Value.ChangeCenter(ownerPos);
					cachedPosForCollider = ownerPos;
				}

				return cachedBounds.Value;
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsCollision(in QuadPointColliderM ohter)
		{
			return Bounds.Intersects(ohter.Bounds);
		}



		int CheckCollisionEventInHashSet(in QuadPointColliderM collider, long curTick, long elapsedTick)
		{

			if (IsTrigger)
			{
				if( collider.IsTrigger == false) // 트리거가 아닌 콜라이더와 충돌했을 때 만 Stay 이벤트를 발생시킴 (유저끼리는 발생 안시킴)
				{
					if (curTick - _lastStayEventTick < OnStayEventDelayTick) // 딜레이가 지나지 않았다면
						return 3; // Stay 이벤트를 발생시키지 않음

					OnTriggerStay(collider, curTick, elapsedTick);
					_lastStayEventTick = curTick;
				}
				
				return 3;
			}
			else
			{

				OnCollisionStay(new CollisionM(this, collider), curTick, elapsedTick);
				return 4;
			}
		}


		// 충돌없음 : 0
		// onTrigerEnter : 1
		// OnCollisionEnter : 2
		// OnTriggerStay : 3
		// onCollisionStay : 4
		// OnTriggerExit : 5
		// OnCollisionExit : 6        
		public int GenCollisionEvent(in Entity objEntity, long curTick, long elapsedTick)
		{
			ref var collider = ref objEntity.Get<QuadPointColliderM>();

			if (IsCollision(collider) == false)
				return 0;

			if (_lastCollisionObjs.Contains(objEntity) == false) // 새롭게 충돌여부 검사
			{
				if (IsTrigger)
				{
					OnTriggerEnter(collider, curTick, elapsedTick);
					_curCollisionObjs.Add(objEntity);
					return 1;
				}
				else
				{
					OnCollisionEnter(new CollisionM(this, collider), curTick, elapsedTick); ;
					_curCollisionObjs.Add(objEntity);
					return 2;
				}
			}
			else
			{
				return CheckCollisionEventInHashSet(collider, curTick, elapsedTick); // 이미 충돌해 있는 녀석이니까 여기서 검사
			}

			return 0;
		}


		public void CollisionEventGenerate(IEnumerable<Entity> entityList, long curTick, long elapsedTick)
		{
			foreach (var objEntity in entityList)
			{
				ref var collider = ref objEntity.Get<QuadPointColliderM>();

				_curCollisionObjs.Add(objEntity);

				if (_lastCollisionObjs.Contains(objEntity) == false) // 기존 충돌하지 않은 거면
				{
					if (IsTrigger)
					{
						OnTriggerEnter(collider, curTick, elapsedTick);
					}
					else
					{
						OnCollisionEnter(new CollisionM(this, collider), curTick, elapsedTick); ;
					}
				}
				else
				{
					CheckCollisionEventInHashSet(collider, curTick, elapsedTick); // 이미 충돌해 있는 녀석이니까 여기서 검사
				}
			}

			foreach (var entityObj in _lastCollisionObjs) // 충돌했다가 벗어난 것들 
			{
				if (entityObj.IsAlive() == false)   // 레퍼런스 하고 있으나 이미 Destory 됐을 수 있음
					continue;

				ref var collider = ref entityObj.Get<QuadPointColliderM>();

				if (_curCollisionObjs.Contains(entityObj) == false)
				{
					if (IsTrigger)
					{
						OnTriggerExit(collider, curTick, elapsedTick);
					}
					else
					{
						OnCollisionExit(new CollisionM(this, collider), curTick, elapsedTick);
					}
				}
			}

			_lastCollisionObjs = _curCollisionObjs;
			_curCollisionObjs = new HashSet<Entity>();

		}


		// 충돌체가 다른 객체와 충돌했을 때 호출되는 메서드

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void OnCollisionEnter(CollisionM collision, long curTick, long elapsedTick)
		{
			Script.OnCollisionEnter(collision, curTick, elapsedTick);
		}


		// 충돌체가 다른 객체와 충돌중일 때 호출되는 메서드
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void OnCollisionStay(CollisionM collision, long curTick, long elapsedTick)
		{
			Script.OnCollisionStay(collision, curTick, elapsedTick);
		}


		// 충돌체가 다른 객체와 충돌이 끝났을 때 호출되는 메서드
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void OnCollisionExit(CollisionM collision, long curTick, long elapsedTick)
		{
			Script.OnCollisionExit(collision, curTick, elapsedTick);
		}

		// 충돌체가 트리거 영역에 진입했을 때 호출되는 메서드
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void OnTriggerEnter(IColliderM other, long curTick, long elapsedTick)
		{
			Script.OnTriggerEnter(other, curTick, elapsedTick);
			Script.SendTriggerEnterPacketToUsers(other, curTick, elapsedTick);
		}

		// 충돌체가 트리거 영역에 머무르고 있을 때 호출되는 메서드
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void OnTriggerStay(IColliderM other, long curTick, long elapsedTick)
		{
			Script.OnTriggerStay(other, curTick, elapsedTick);
			Script.SendTriggerStayPacketToUsers(other, curTick, elapsedTick);
		}

		// 충돌체가 트리거 영역에서 벗어났을 때 호출되는 메서드
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void OnTriggerExit(IColliderM other, long curTick, long elapsedTick)
		{
			Script.OnTriggerExit(other, curTick, elapsedTick);
		}
	}

	/// <summary>
	/// 충돌한 객체의 Collider와 그 정보를 가진 클래스
	/// </summary>
	public class CollisionM
	{

		// 충돌한 객체의 Collider 컴포넌트를 가져오는 프로퍼티
		public IColliderM _collider { get; }

		public CollisionM(IColliderM thisCollider, IColliderM otherCollider)//, ContactPoint[] contacts, Vector3 relativeVelocity)
		{
			this._collider = thisCollider;
			ContactPos = new ContactPoint(thisCollider, otherCollider);
		}

		public ContactPoint ContactPos { get; set; }

		// 충돌한 지점들의 정보를 가져오는 프로퍼티
		//public ContactPoint[] contacts { get; }

		// 충돌 발생 시의 상대 속도를 가져오는 프로퍼티
		//public Vector3 relativeVelocity { get; }

	}


	public struct ContactPoint
	{
		public ContactPoint(IColliderM thisCollider, IColliderM otherCollider)
		{
			_thisCollider = thisCollider;
			_otherCollider = otherCollider;

			_point = Vector3.Abs(thisCollider.Bounds.Center - otherCollider.Bounds.Center) / 2f; // 중돌 지점
			_normal = Vector3.Normalize(_point);
			//var rectContact = thisCollider._bounds.GetRectIntersects(otherCollider._bounds);

			//if (rectContact.Width != 0 && rectContact.Height != 0)
			//    _point = new Vector3(rectContact.X + rectContact.Width / 2, rectContact.Y + rectContact.Height / 2, 0);            
		}

		// 충돌 지점의 위치
		public Vector3 _point;

		// 충돌 지점의 법선 벡터
		public Vector3 _normal;

		// 충돌 중인 첫 번째 객체의 Collider 컴포넌트
		public IColliderM _thisCollider;

		// 충돌 중인 두 번째 객체의 Collider 컴포넌트
		public IColliderM _otherCollider;
	}

}
