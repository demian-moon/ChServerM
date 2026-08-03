using Arch.Core;
using Arch.Core.Extensions;
using Collections.Pooled;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Policy;
using System.Threading;

namespace EcsServerLibM
{

	/// <summary>
	/// 서버틱 send 관리  
	/// </summary>    
	public struct LastServerTickSendM
	{
		long lastServerTickSend;
		long serverTickSendInterval;

		public LastServerTickSendM(long serverTickSendInterval)
		{
			this.serverTickSendInterval = serverTickSendInterval;
		}

		public void SetSendTime()
		{
			lastServerTickSend = Stopwatch.GetTimestamp();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool IsSendTime(long curTick)
		{
			if (lastServerTickSend == 0 || curTick - lastServerTickSend >= serverTickSendInterval)
				return true;

			return false;
		}

	}

	public interface IShapeM
	{
		bool Contains(IShapeM otherShape);
		bool Intersects(IShapeM otherShape);
		//void Rotate(float angle);
		Vector3[] GetAxes();
		(float Min, float Max) ProjectOntoAxis(Vector3 axis);
		Vector3 Center { get; }
		Vector3[] Points { get; } // 사각형의 꼭지점
	}


	public struct QuadPointBoundM : IShapeM
	{
		private Vector3[] _points; // 사각형의 네 꼭지점 좌표 배열
		public readonly Vector3[] Points => _points; // 외부에서 네 꼭지점 배열 접근 가능

		bool bAxisAligned;

		// 축에 정렬 됐을 때 SAT 축
		readonly Vector3[] _axesAxisAligned = { new Vector3(1, 0, 0), new Vector3(0, 1, 0) };

		Vector3[] _axes; // SAT에 쓰이는 축

		// 네 개의 꼭지점으로 사각형 초기화
		public QuadPointBoundM(Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4)
		{
			_points = new Vector3[] { p1, p2, p3, p4 };
			Center = new Vector3((_points[0].X + _points[1].X + _points[2].X + _points[3].X) / 4,
								   (_points[0].Y + _points[1].Y + _points[2].Y + _points[3].Y) / 4,
								   0);
		}

		// 사각형의 중심 좌표 계산
		public Vector3 Center
		{
			get; private set;
		}

		// RectM처럼 AABB 축에 정렬된 사각형인지 검사
		public bool IsAxisAlignedBoundingBox()
		{
			// Points 배열에서 바로 각 꼭지점의 X, Y 값을 확인
			return Points[0].X == Points[1].X && Points[2].X == Points[3].X
				&& Points[0].Y == Points[3].Y && Points[1].Y == Points[2].Y;
		}

		public static bool AABBIntersects(QuadPointBoundM a, QuadPointBoundM b)
		{
			// QuadPointBoundM의 Points 배열을 기준으로 AABB 충돌 검사

			// 첫 번째 사각형 (a) - 각 점을 기준으로 AABB 비교
			float aMinX = a.Points[0].X;
			float aMaxX = a.Points[2].X;
			float aMinY = a.Points[0].Y;
			float aMaxY = a.Points[2].Y;

			// 두 번째 사각형 (b) - 각 점을 기준으로 AABB 비교
			float bMinX = b.Points[0].X;
			float bMaxX = b.Points[2].X;
			float bMinY = b.Points[0].Y;
			float bMaxY = b.Points[2].Y;

			// AABB 충돌 검사: 두 AABB가 겹치면 true 반환
			return aMaxX > bMinX && aMinX < bMaxX && aMaxY > bMinY && aMinY < bMaxY;
		}

		public static bool AABBIntersects(QuadPointBoundM a, RectM b)
		{
			// QuadPointBoundM의 AABB 범위
			float aMinX = a.Points[0].X;
			float aMaxX = a.Points[2].X;
			float aMinY = a.Points[0].Y;
			float aMaxY = a.Points[2].Y;

			// RectM의 AABB 범위
			float bMinX = b.Left;
			float bMaxX = b.Right;
			float bMinY = b.Bottom;
			float bMaxY = b.Top;

			// AABB 충돌 검사: 두 AABB가 겹치면 true 반환
			return aMaxX > bMinX && aMinX < bMaxX && aMaxY > bMinY && aMinY < bMaxY;
		}

		// 다른 도형과 현재 사각형이 교차하는지 확인
		public bool Intersects(IShapeM otherShape)
		{
			if (this.bAxisAligned && otherShape is RectM rect) // 다른 도형이 RectM 유형인 경우
			{
				return AABBIntersects(this, rect);
			}
			else if (this.bAxisAligned && otherShape is QuadPointBoundM quad && quad.bAxisAligned)
			{
				AABBIntersects(this, quad);
			}
			else
			{
				var axes = otherShape.GetAxes();    // 렉트M의 축을 구한다

				// 축에 따라 두 도형의 각 꼭지점을 Projection 시킨후 Min Max 비교
				foreach(var axis in axes)
				{
					var rtMinMax = otherShape.ProjectOntoAxis(axis);
					var minMax = ProjectOntoAxis(axis);

					// 두 사각형의 투영 범위가 겹치는지 확인
					if( (rtMinMax.Max < minMax.Min || minMax.Max < rtMinMax.Min) == true)
					{
						return false;
					}
				}

				axes = GetAxes();
				foreach(var axis in axes)
				{
					var rtMinMax = otherShape.ProjectOntoAxis(axis);
					var minMax = ProjectOntoAxis(axis);

					// 두 사각형의 투영 범위가 겹치는지 확인
					if ((rtMinMax.Max < minMax.Min || minMax.Max < rtMinMax.Min) == true)
					{
						return false;
					}
				}
				
				return true; // 모든 축에서 투영이 겹치면 교차함
			}
			return false; // 다른 도형 타입의 경우 false 반환
		}

		// 축 얻기
		public Vector3[] GetAxes()
		{
			if (bAxisAligned)
				return _axesAxisAligned;

			if (_axes != null)
				return _axes;

			// 첫 번째 축 (첫 번째 변)
			Vector3 edge1 = _points[1] - _points[0]; // 첫 번째 변
			//axes[0] = new Vector3(-edge1.Y, edge1.X, 0); // 법선 벡터 (정규화하지 않음)

			// 두 번째 축 (두 번째 변)
			Vector3 edge2 = _points[2] - _points[1]; // 두 번째 변
													 //axes[1] = new Vector3(-edge2.Y, edge2.X, 0); // 법선 벡터 (정규화하지 않음)

			_axes = new Vector3[] { new Vector3(-edge1.Y, edge1.X, 0), new Vector3(-edge2.Y, edge2.X, 0) };
			return _axes;
		}

		// Rotation으로 축이 변했을 때 리셋하는 함수
		public void ResetAxes()
		{
			_axes = null;
		}

		// Projection 결과를 반환하는 함수
		public (float Min, float Max) ProjectOntoAxis(Vector3 axis)
		{
			if (bAxisAligned)
			{
				if (axis == new Vector3(1, 0, 0)) // x축
				{
					return (_points[0].X, _points[2].X);
				}
				else if (axis == new Vector3(0, 1, 0)) // y축
				{
					return (_points[0].Y, _points[2].Y);
				}
			}

			// 2D 평면에서의 x, y만 사용
			Vector2 axis2D = new Vector2(axis.X, axis.Y);
			float min = float.MaxValue;
			float max = float.MinValue;

			foreach (var point in _points)
			{
				Vector2 point2D = new Vector2(point.X, point.Y);
				float projection = Vector2.Dot(point2D, axis2D); // 축에 투영
				min = MathF.Min(min, projection);
				max = MathF.Max(max, projection);
			}

			return (min, max);
		}



		// 회전된 사각형의 최대/최소 X, Y값 계산 함수
		public (float minX, float maxX, float minY, float maxY) GetBoundingBox()
		{
			float minX = float.MaxValue;
			float maxX = float.MinValue;
			float minY = float.MaxValue;
			float maxY = float.MinValue;

			foreach (var point in _points)
			{
				minX = MathF.Min(minX, point.X);
				maxX = MathF.Max(maxX, point.X);
				minY = MathF.Min(minY, point.Y);
				maxY = MathF.Max(maxY, point.Y);
			}

			return (minX, maxX, minY, maxY);
		}

		// 다른 도형이 현재 사각형에 포함되는지 확인
		public bool Contains(IShapeM otherShape)
		{
			if (this.bAxisAligned && otherShape is RectM rt)
			{
				// QuadPointBoundM이 AABB이고 RectM도 AABB이므로 좌표 비교로 포함 여부 확인
				float minX = this.Points[0].X;  // QuadPointBoundM의 최소 X 좌표
				float maxX = this.Points[2].X;  // QuadPointBoundM의 최대 X 좌표
				float minY = this.Points[0].Y;  // QuadPointBoundM의 최소 Y 좌표
				float maxY = this.Points[2].Y;  // QuadPointBoundM의 최대 Y 좌표

				// RectM의 좌표
				float rectMinX = rt.Left;
				float rectMaxX = rt.Right;
				float rectMinY = rt.Bottom;
				float rectMaxY = rt.Top;

				// RectM이 QuadPointBoundM에 완전히 포함되는지 확인
				return rectMinX > minX && rectMaxX < maxX && rectMinY > minY && rectMaxY < maxY;
			}
			else if (otherShape is RectM rect) // 다른 도형이 RectM 유형인 경우
			{
				// 사각형 모서리 4개가 모두 현재 사각형에 포함되는지 확인
				Vector3[] corners = {
				new Vector3(rect.Left, rect.Bottom, 0),
				new Vector3(rect.Left, rect.Top, 0),
				new Vector3(rect.Right, rect.Top, 0),
				new Vector3(rect.Right, rect.Bottom, 0)

			};

				foreach (var corner in corners)
				{
					if (!Contains(corner)) return false; // 하나라도 포함되지 않으면 false 반환
				}
				return true;
			}
			else if (this.bAxisAligned && otherShape is QuadPointBoundM qb && qb.bAxisAligned)
			{
				float minX = this.Points[0].X;  // QuadPointBoundM의 최소 X 좌표
				float maxX = this.Points[2].X;  // QuadPointBoundM의 최대 X 좌표
				float minY = this.Points[0].Y;  // QuadPointBoundM의 최소 Y 좌표
				float maxY = this.Points[2].Y;  // QuadPointBoundM의 최대 Y 좌표

				float otherMinX = qb.Points[0].X;  // other QuadPointBoundM 최소 X 좌표
				float otherMaxX = qb.Points[2].X;  // other QuadPointBoundM 최대 X 좌표
				float otherMinY = qb.Points[0].Y;  // other QuadPointBoundM 최소 Y 좌표
				float otherMaxY = qb.Points[2].Y;  // other QuadPointBoundM 최대 Y 좌표

				// 완전히 포함되는지 확인
				return otherMinX > minX && otherMaxX < maxX && otherMinY > minY && otherMaxY < maxY;

			}
			else if (otherShape is QuadPointBoundM quad) // 다른 도형이 QuadPointBoundM 유형인 경우
			{
				foreach (var point in quad.Points)
				{
					if (!Contains(point)) return false; // 네 꼭지점 중 하나라도 포함되지 않으면 false 반환
				}
				return true;
			}
			return false; // 다른 도형 타입의 경우 false 반환
		}

		/// <summary>
		/// 주어진 점이 사각형 내부에 있는지 확인
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private bool Contains(Vector3 pt)
		{
			// 점이 포함될 수 있는지 확인하기 위해 두 개의 삼각형으로 나눔			
			Vector3 p1 = Points[1];
			Vector3 p2 = Points[2];

			// 첫 번째 삼각형 (p0, p1, p2) - Points[1]과 Points[2] 사이 선분을 제외
			p1.Y -= 1;     // Points[1]의 Y값을 -1
			p2.X -= 1;     // Points[2]의 X값을 -1
			p2.Y -= 1;     // Points[2]의 Y값을 -1
			if (PointInTriangle(pt, Points[0], p1, p2))
				return true;

			Vector3 p3 = Points[3];
			p3.X -= 1;     // Points[3]의 X값을 -1

			return PointInTriangle(pt, Points[0], p2, p3);
		}

		// 점이 삼각형 내부에 있는지 확인 (삼각형을 두 개로 나누어 포함 여부 검사)
		private static bool PointInTriangle(Vector3 pt, Vector3 a, Vector3 b, Vector3 c)
		{
			Vector2 v0 = new Vector2(c.X - a.X, c.Y - a.Y);
			Vector2 v1 = new Vector2(b.X - a.X, b.Y - a.Y);
			Vector2 v2 = new Vector2(pt.X - a.X, pt.Y - a.Y);

			float dot00 = Vector2.Dot(v0, v0);
			float dot01 = Vector2.Dot(v0, v1);
			float dot02 = Vector2.Dot(v0, v2);
			float dot11 = Vector2.Dot(v1, v1);
			float dot12 = Vector2.Dot(v1, v2);

			float invDenom = 1 / (dot00 * dot11 - dot01 * dot01);
			float u = (dot11 * dot02 - dot01 * dot12) * invDenom;
			float v = (dot00 * dot12 - dot01 * dot02) * invDenom;
			return (u >= 0) && (v >= 0) && (u + v < 1); // u, v가 0 이상이고 u + v < 1이면 삼각형 내부
		}


		// ChangeCenter 함수: 주어진 새로운 중심으로 4개의 점을 이동시킴
		public void ChangeCenter(PositionM newCenter)
		{
			// 중심 이동 차이 계산
			Vector3 offset = newCenter.V3 - Center;  // V3가 Vector3인지 확인

			// 모든 점에 그 차이만큼 이동
			for (int i = 0; i < _points.Length; i++)
			{
				_points[i] += offset;
			}

			// 새로운 중심으로 업데이트
			Center = newCenter.V3;
		}

		// 현재 사각형을 주어진 각도로 회전
		public void Rotate(float angle)
		{
			float radians = MathF.PI * angle / 180f; // 각도를 라디안으로 변환
			float cos = MathF.Cos(radians); // 회전의 코사인 값
			float sin = MathF.Sin(radians); // 회전의 사인 값


			for (int i = 0; i < _points.Length; i++)
			{
				// 각 점을 중심점 기준으로 회전
				float dx = _points[i].X - Center.X; // 회전축 중심 Center
				float dy = _points[i].Y - Center.Y;

				_points[i].X = cos * dx - sin * dy + Center.X;
				_points[i].Y = sin * dx + cos * dy + Center.Y;
			}
		}

	}


	/// <summary>
	/// 충돌을 판단하는데 쓰는 Rect
	/// </summary>
	public struct RectM : IShapeM
	{
		public float X;
		public float Y;
		public float Width;
		public float Height;

		Vector3[] _points; // 시계방향 꼭지점
		public Vector3[] Points { get { return _points; } }

		readonly public float Left => X;
		readonly public float Right => X + Width;
		readonly public float Top => Y + Height;
		readonly public float Bottom => Y;

		readonly Vector3[] _axes = { new Vector3(1, 0, 0), new Vector3(0, 1, 0) };

		public Vector3 CenterLocal
		{
			get
			{
				return new Vector3(Width / 2f, Height / 2f, 0);
			}
		}

		Vector3 _center;
		public Vector3 Center { get { return _center; } set { _center = value; } }

		public RectM(float x, float y, float width, float height)
		{
			X = x;
			Y = y;

			_center.X = X + (float)Math.Ceiling(width / 2f); // width가 0일때 Center.X는 X이어야 함
			_center.Y = Y + (float)Math.Ceiling(height / 2f);

			Width = Math.Max(1, width);
			Height = Math.Max(1, height);

			_points = new Vector3[] {new Vector3(x, y, 0), new Vector3(x, y + Height, 0),
				new Vector3(x + Width, y + Height, 0), new Vector3(x + Width, y, 0)};
		}

		public RectM(in PositionM centerPos, float width, float height)
		{
			float halfWidth = (float)Math.Ceiling(width / 2f);
			float halfHeight = (float)Math.Ceiling(height / 2f);

			X = centerPos.X - halfWidth;    // width가 0일때 X는 0이어야 함
			Y = centerPos.Y - halfHeight;

			Width = Math.Max(1, width);
			Height = Math.Max(1, height);

			_center.X = centerPos.X;
			_center.Y = centerPos.Y;

			_points = new Vector3[] {new Vector3(X, Y, 0), new Vector3(X, Y + Height, 0),
				new Vector3(X + Width, Y + Height, 0), new Vector3(Y + Width, Y, 0)};
		}

		/// <summary>
		/// 매개변수로 주어진 Rect가 포함되어 있는지
		/// </summary>
		/// <param name="rect"></param>
		/// <returns></returns>
		public bool Contains(IShapeM otherShape)
		{
			bool bContain = false;

			if (otherShape is RectM)
			{
				var rect = (RectM)otherShape;
				bContain = !(rect.X < X ||
						rect.X + rect.Width > X + Width ||
						rect.Y < Y ||
						rect.Y + rect.Height > Y + Height);
			}
			return bContain;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool Intersects(IShapeM otherShape)
		{
			if (otherShape is RectM rect)
			{
				// AABB vs AABB - 가장 빠른 방법
				return !(rect.X >= X + Width ||
						rect.X + rect.Width <= X ||
						rect.Y >= Y + Height ||
						rect.Y + rect.Height <= Y);
			}
			else if (otherShape is QuadPointBoundM qpBound)
			{
				// QuadPointBoundM의 Intersects 메서드 활용
				// 이미 최적화된 SAT 알고리즘 사용
				return qpBound.Intersects(this);
			}
			return false;
		}

		// Projection 결과를 반환하는 함수
		public (float Min, float Max) ProjectOntoAxis(Vector3 axis)
		{
			if (axis == new Vector3(1, 0, 0)) // x축
			{
				return (_points[0].X, _points[2].X);
			}
			else if (axis == new Vector3(0, 1, 0)) // y축
			{
				return (_points[0].Y, _points[2].Y);
			}

			// 2D 평면에서의 x, y만 사용
			Vector2 axis2D = new Vector2(axis.X, axis.Y);
			float min = float.MaxValue;
			float max = float.MinValue;

			foreach (var point in _points)
			{
				Vector2 point2D = new Vector2(point.X, point.Y);
				float projection = Vector2.Dot(point2D, axis2D); // 축에 투영
				min = MathF.Min(min, projection);
				max = MathF.Max(max, projection);
			}

			return (min, max);
		}


		/// <summary>
		/// 매개변수로 주어진 Rect가 포함되어 있는지
		/// </summary>
		/// <param name="rect"></param>
		/// <returns></returns>
		public bool Contains(in RectM rect)
		{
			return !(rect.X < X ||
					rect.X + rect.Width > X + Width ||
					rect.Y < Y ||
					rect.Y + rect.Height > Y + Height);
		}


		/// <summary>
		/// 해당 pos가 Rect안에 있는 점인지
		/// 주의 !! : Right와 Top 좌표는 없 걸로 판단한다.
		/// </summary>
		/// <param name="pos"></param>
		/// <returns></returns>
		public bool Contains(in Vector3 pos)
		{
			return !(pos.X < Left ||
						pos.X >= Right ||
						pos.Y < Bottom ||
						pos.Y >= Top);

		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool Contains(in PositionM pos)
		{
			return !(pos.X < Left ||
						pos.X >= Right ||
						pos.Y < Bottom ||
						pos.Y >= Top);
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool Intersects(in RectM rect)
		{
			return !(rect.X >= X + Width ||
					 rect.X + rect.Width <= X ||
					 rect.Y >= Y + Height ||
					 rect.Y + rect.Height <= Y);
		}


		/// <summary>
		/// 0, 0보다 크고 maxWidth, maxHeight보다 사이에서 offset 
		/// 주의 사항 !! : Rect크기는 그대로 이니 주의 할 것
		/// </summary>
		/// <param name="offsetX"></param>
		/// <param name="offsetY"></param>
		/// <param name="maxWidth"></param>
		/// <param name="maxHeight"></param>
		/// <returns></returns>
		public RectM OffsetWithinSize(float offsetX, float offsetY, float maxWidth, float maxHeight)
		{
			float xSumOffset = X + offsetX;
			float ySumOffset = Y + offsetY;

			if (xSumOffset >= 0 && ySumOffset >= 0 && xSumOffset <= maxWidth - Width && ySumOffset <= maxHeight - Height)
				return new RectM(xSumOffset, ySumOffset, Width, Height);

			float tX = Math.Min(maxWidth - Width, Math.Max(0, xSumOffset));
			float tY = Math.Min(maxHeight - Height, Math.Max(0, ySumOffset));

			return new RectM(tX, tY, Width, Height);
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="offsetX"></param>
		/// <param name="offsetY"></param>
		/// <returns></returns>
		public RectM Offset(float offsetX, float offsetY)
		{
			return new RectM(X + offsetX, Y + offsetY, Width, Height);
		}

		public void ChangeCenter(in PositionM newCenter)
		{
			// 중심 이동 차이 계산
			Vector3 offset = newCenter.V3 - Center;  // V3가 Vector3인지 확인

			// 모든 점에 그 차이만큼 이동
			for (int i = 0; i < _points.Length; i++)
			{
				_points[i] += offset;
			}

			X = _points[0].X;
			Y = _points[0].Y;

			_center.X = newCenter.X;
			_center.Y = newCenter.Y;
		}

		/// <summary>
		/// Rect의 크기는 유지 하면서 맵 밖을 벗어나지 않는 스크린 사이즈를 다시 계산 하기 위한 함수
		/// 기능 자체는 maxWidth, maxHeight를 벗어 나지 않는 Rect 범위 재 설정
		/// </summary>
		/// <param name="maxWidth"></param>
		/// <param name="maxHeight"></param>
		/// <returns></returns>
		public RectM ReCalcRectWithinSize(int maxWidth, int maxHeight)
		{
			if (X >= 0 && Y >= 0 && Right <= maxWidth && Top <= maxHeight)
				return this;

			float tX = Math.Min(maxWidth - Width, Math.Max(0, X));
			float tY = Math.Min(maxHeight - Height, Math.Max(0, Y));
			return new RectM(tX, tY, Width, Height);
		}


		/// <summary>
		/// 가운데 중점을 기준으로 Rect의 범위를 조정 (x, y, Width, Height)
		/// 마이너스 값도 처리 함
		/// </summary>
		/// <param name="addSubXsize"></param>
		/// <param name="addSubYsize"></param>
		public void ChangeSize(float addSubXsize, float addSubYsize)
		{
			float halfXsize = addSubXsize / 2.0f;
			float halfYsize = addSubXsize / 2.0f;

			X -= halfXsize; // x, y는 마이너스 하는게 늘리는 것
			Y -= halfYsize;
			Width += addSubXsize;     // 총사이즈를 줄이는 것이므로 half가 아님
			Height += addSubYsize;
		}

		/// <summary>
		/// 중심을 기준으로 Width, Height의 값을 바꿈
		/// </summary>        
		public void SetSize(float newWidth, float newHeight)
		{
			ChangeSize(newWidth - Width, newHeight - Height);            
		}

		/// <summary>
		/// 렉트의 크기를 조정, 최종 
		/// </summary>
		/// <param name="addSubLeft"></param>
		/// <param name="addSubTop"></param>
		/// <param name="addSubRight"></param>
		/// <param name="addSubBottom"></param>
		public void ChangeSizeInMax(float addSubLeft, float addSubTop, float addSubRight, float addSubBottom, float rightMax, float topMax)
		{

			if (addSubLeft < 0)
			{
				addSubLeft = -addSubLeft;   // 양수
				addSubLeft = Math.Min(addSubLeft, X);
				X -= addSubLeft;
				Width += addSubLeft;    // 폭 늘리기
			}
			else
			{
				addSubLeft = Math.Min(addSubLeft, Width - 1);
				X += addSubLeft;
				Width -= addSubLeft;    // addSubLeft값이 양수면 - 폭 줄이기
			}

			if (addSubTop < 0) // 음수면 높이 줄이는것
			{
				addSubTop = -addSubTop; // 양수로
				addSubTop = Math.Min(addSubTop, Height - 1);
				Height -= addSubTop;
			}
			else
			{
				addSubTop = Math.Min(addSubTop, topMax - Top);
				Height += addSubTop;
			}

			if (addSubRight < 0)
			{
				addSubRight = -addSubRight; // 양로
				addSubRight = Math.Min(addSubRight, Width - 1);
				Width -= addSubRight;
			}
			else
			{
				addSubRight = Math.Min(addSubRight, rightMax - Right);
				Width += addSubRight;
			}

			if (addSubBottom < 0)
			{
				addSubBottom = -addSubBottom; // 양수로
				addSubBottom = Math.Min(addSubBottom, Y);
				Y -= addSubBottom;
				Height += addSubBottom;
			}
			else
			{
				addSubBottom = Math.Min(addSubBottom, Height - 1);
				Y += addSubBottom;
				Height -= addSubBottom;
			}

			_center.X = X + (float)Math.Ceiling(Width / 2f); // width가 0일때 Center.X는 X이어야 함
			_center.Y = Y + (float)Math.Ceiling(Height / 2f);
		}

		/// <summary>
		/// Rotation 후 RectM 리턴
		/// </summary>
		/// <param name="angle"></param>
		/// <returns></returns>
		public RectM GetBoundingRectAfterRotation(float angle)
		{
			// 노말라이즈 
			angle = MathM.NormalizeAngle(angle);

			// 각도를 라디안으로 변환
			float radian = angle * MathF.PI / 180f;
			float cos = MathF.Cos(radian);
			float sin = MathF.Sin(radian);

			// 회전된 폭과 높이 계산 (바운딩 박스)
			float rotatedWidth = MathF.Abs(Width * cos) + MathF.Abs(Height * sin);
			float rotatedHeight = MathF.Abs(Width * sin) + MathF.Abs(Height * cos);

			// 바운딩 박스의 좌상단 기준으로 새로운 좌표 반환
			float newX = _center.X - rotatedWidth / 2;
			float newY = _center.Y - rotatedHeight / 2;

			return new RectM(newX, newY, rotatedWidth, rotatedHeight);
		}

		

		public void Rotate(float angle)
		{
			throw new NotImplementedException();
		}
		public Vector3[] GetAxes()
		{
			// AABB는 항상 X축과 Y축을 축으로 사용합니다.
			return _axes;
		}

	}


	/// <summary>
	/// 위치 포지션
	/// </summary>
	public struct PositionM : IEquatable<PositionM>
	{
		public float X;
		public float Y;
		public float Z;

		public PositionM(float x, float y, float z)
		{
			X = x; Y = y; Z = z;
		}

		public PositionM(in Vector3 pos)
		{
			X = pos.X; Y = pos.Y; Z = pos.Z;
		}


		public Vector3 V3
		{
			get => new Vector3(X, Y, Z);
		}

		public bool Equals(PositionM other)
		{
			return (X == other.X && Y == other.Y && Z == other.Z);
		}

		override public bool Equals(object other)
		{
			if (other is PositionM)
			{
				return Equals((PositionM)other);
			}
			return false;
		}

		public static PositionM operator +(PositionM lhs, Vector3 rhs)
		{
			return new PositionM(lhs.X + rhs.X, lhs.Y + rhs.Y, lhs.Z + rhs.Z);
		}

		public static PositionM operator +(PositionM lhs, PositionM rhs)
		{
			return new PositionM(lhs.X + rhs.X, lhs.Y + rhs.Y, lhs.Z + rhs.Z);
		}

		//public static PositionM operator -(PositionM lhs, Vector3 rhs)
		//{
		//    return new PositionM(lhs.X - rhs.X, lhs.Y - rhs.Y, lhs.Z - rhs.Z);
		//}

		public static Vector3 operator -(PositionM lhs, Vector3 rhs)
		{
			return new Vector3(lhs.X - rhs.X, lhs.Y - rhs.Y, lhs.Z - rhs.Z);
		}

		public static PositionM operator -(PositionM lhs, PositionM rhs)
		{
			return new PositionM(lhs.X - rhs.X, lhs.Y - rhs.Y, lhs.Z - rhs.Z);
		}

		// == 연산자 오버로딩
		public static bool operator ==(PositionM lhs, PositionM rhs)
		{
			return lhs.Equals(rhs);
		}

		// != 연산자 오버로딩
		public static bool operator !=(PositionM lhs, PositionM rhs)
		{
			return !lhs.Equals(rhs);
		}


		public void SetPos(Vector3 rotation, double distance)
		{
			SetPos(rotation.X, distance);
		}

		public void SetPos(double angle, double distance)
		{
			double angleInRadians = angle * MathM.Deg2Rad;
			// x, y 좌표 계산                
			double deltaX = distance * Math.Cos(angleInRadians);
			double deltaY = distance * Math.Sin(angleInRadians);

			var newPosX = X + (float)deltaX;
			var newPosY = Y + (float)deltaY;

			SetPos(newPosX, newPosY, 0);
		}

		public void SetPos(in PositionM pos)
		{
			X = pos.X; Y = pos.Y; Z = pos.Z;
		}



		public void SetPos(float x, float y, float z)
		{
			X = x; Y = y; Z = z;
		}

		public override int GetHashCode()
		{
			// 초기 해시 코드 상수값
			int hash = 17;

			// 각 필드의 해시 코드를 소수와 함께 조합
			hash = hash * 23 + X.GetHashCode();
			hash = hash * 23 + Y.GetHashCode();
			hash = hash * 23 + Z.GetHashCode();

			return hash;
		}
	}


	/// <summary>
	/// 로테이션 
	/// </summary>
	public struct RotationM : IEquatable<RotationM>
	{
		public float X;
		public float Y;
		public float Z;

		public RotationM(float x, float y, float z)
		{
			X = x; Y = y; Z = z;
		}

		public bool Equals(RotationM other)
		{
			return (X == other.X && Y == other.Y && Z == other.Z);
		}

		override public bool Equals(object other)
		{
			if (other is RotationM)
			{
				return Equals((RotationM)other);
			}
			return false;
		}

		// == 연산자 오버로딩
		public static bool operator ==(RotationM lhs, RotationM rhs)
		{
			return lhs.Equals(rhs);
		}

		// != 연산자 오버로딩
		public static bool operator !=(RotationM lhs, RotationM rhs)
		{
			return !lhs.Equals(rhs);
		}

		public void SetRotation(float x, float y, float z)
		{
			X = x; Y = y; Z = z;
		}


		public void SetRotation(in RotationM rotation)
		{
			if (this == rotation)
				return;

			X = rotation.X; Y = rotation.Y; Z = rotation.Z;

		}

		public RotationM GetRotation()
		{
			return this;
		}

		public float GetAngle()
		{
			return X;
		}

		public override int GetHashCode()
		{
			// 초기 해시 코드 상수값
			int hash = 47;

			// 각 필드의 해시 코드를 소수와 함께 조합
			hash = hash * 23 + X.GetHashCode();
			hash = hash * 23 + Y.GetHashCode();
			hash = hash * 23 + Z.GetHashCode();

			return hash;
		}
	}

	/// <summary>
	/// 게임오브젝트의 이미지를 나타내는 컴포넌트
	/// Bounds를 가지며 LQuadTree 상에 이 바운딩박스의 크기로 등록되어 있다
	/// </summary>
	public struct ImgSizeM
	{
		public RectM cachedBounds;
		private PositionM cachedPosForImgSize;

		public BaseGameObjM _owner;

		float _angle;

		
		public SizeM _obbSize; // 오리지날 바운딩 사이즈 (최초 사이즈)

		public ImgSizeM(BaseGameObjM owner, SizeM size)
		{
			_owner = owner;            
			_obbSize = size;  
			
			cachedBounds = new RectM(new PositionM(), _obbSize.X, _obbSize.Y);
		}


		// 이미지의 Bounding Box 정보를 가져오는 프로퍼티
		public RectM Bounds
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get
			{
				var ownerPos = _owner.GetPos();
				if (cachedPosForImgSize != ownerPos)
				{
					cachedBounds.ChangeCenter(ownerPos);
					cachedPosForImgSize = ownerPos;
				}

				return cachedBounds;
			}
		}

		/// <summary>
		/// 현재 각도에서 angle만큼 OBB(original Bounding Box)를 로테이션 시킨다
		/// 주의 이미지의 사이가 변경되었으므로 LQuadTree를 갱신(Update)해야 한다
		/// </summary>
		/// <param name="angle"></param>
		public void RotationObb(float angle)
		{
			_angle += angle;
			_angle = MathM.NormalizeAngle(angle);

			var bbSize = MathM.GetBoundingSizeAfterRotation(_angle, _obbSize.X, _obbSize.Y);
			cachedBounds.SetSize(bbSize.Width, bbSize.Height); // 캐시 bounds사이즈 변경, 중심부터 변경되어야 함
		}        
	}


	public struct SizeM : IEquatable<SizeM>
	{
		public float X;
		public float Y;
		public float Z;

		public SizeM(float x, float y, float z)
		{
			X = x; Y = y; Z = z;
		}

		public bool Equals(SizeM other)
		{
			return (X == other.X && Y == other.Y && Z == other.Z);
		}

		public override bool Equals(object obj)
		{
			return obj is SizeM && Equals((SizeM)obj);
		}

		public static bool operator ==(SizeM left, SizeM right)
		{
			return left.Equals(right);
		}

		public static bool operator !=(SizeM left, SizeM right)
		{
			return !left.Equals(right);
		}

		public void SetSize(float x, float y, float z)
		{
			X = x; Y = y; Z = z;
		}

		public void SetSize(SizeM size)
		{
			X = size.X; Y = size.Y; Z = size.Z;
		}


		public void SetSize(in Vector3 size)
		{
			//needPkUpdateFlag = true;   // 필요한지 검토 해야 함
		}

		public override int GetHashCode()
		{
			// 초기 해시 코드 상수값
			int hash = 47;

			// 각 필드의 해시 코드를 소수와 함께 조합
			hash = hash * 23 + X.GetHashCode();
			hash = hash * 23 + Y.GetHashCode();
			hash = hash * 23 + Z.GetHashCode();

			return hash;
		}
	}

	/// <summary>
	/// 패킷을 보내야 될 필요가 있는지 체크하는 구조체
	/// </summary>
	public struct NeedPkSendM
	{        
		public bool CheckNeedPkSend
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get
			{
				return DirChange || Stopping
					|| ImgRotate || LocatePos || MovingStart;
			}
		}

		public bool Stopping { get; set; } = true; // 멈춤 행위를 함
		public bool MovingStart { get; set; } // 멈췄다 다시 움직임
		public bool ImgRotate { get; set; } // 이미지 회전
		public bool DirChange { get; set; } // 방향 전환
		public bool LocatePos {  get; set; } // 위치 변경

		public NeedPkSendM()
		{

		}

		public void Reset()
		{
			Stopping = true; 
			MovingStart = false;
			ImgRotate = false;
			DirChange = false;
			LocatePos = false;
		}
		

	}


	public enum TEAM_NUMBER { OUR_SIDE, ENEMY }
	public struct TeamNumberM
	{
		public TEAM_NUMBER team;

		public TeamNumberM(TEAM_NUMBER team)
		{
			this.team = team;
		}
	}

	//// 패킷 보내기 위한
	//public class ObjInfoForPkM
	//{
	//    public readonly ObjBasicDataM basic;
	//    public readonly PositionM pos;
	//    public readonly RotationM rot;

	//    public ObjInfoForPkM(in Entity entity)
	//    {
	//        var entityData = entity.Get<ObjBasicDataM, PositionM, RotationM>();

	//        this.basic = entityData.t0;
	//        this.pos = entityData.t1;
	//        this.rot = entityData.t2;
	//    }
	//}

	// 기본 참조 데이터 (최대한 작게 구성)
	public struct ObjBasicDataM
	{
		public int objType;     // 1 : 유저, 2 : 몬스터, 3 : 총알, 4 : 아이템
		public long Oid;
		public string Name;
		public int idxCreateId;

		public List<SparseSetM<Entity>> referQuadGrids;

		public ObjBasicDataM(int objType, long oid, string name, int idxCreateId)
		{
			this.objType = objType;
			this.Oid = oid;
			this.Name = name;
			this.idxCreateId = idxCreateId;

			referQuadGrids = new List<SparseSetM<Entity>>();
		}
	}

	public struct SrvUserDataM
	{
		public SrvUserM srvUser;
		public SrvUserDataM(SrvUserM srvUser)
		{
			this.srvUser = srvUser;
		}
	}

	/// <summary>
	/// fixedUpdate 전에 먼저 움직인것 처리 (유저가 움직였거나, Locate등으로 이동 했을 때)
	/// 그 tick을 저장해 놨다가 업데이트 때 너무 많이 움직이지 않도록 보정하는 스트럭쳐
	/// </summary>
	public struct LastMoveTickM
	{
		bool needGridUpdate;
		public bool NeedGridUpdate
		{
			get
			{
				if (NeedMoveFlag)
					return true;
				return needGridUpdate;
			}
			set
			{
				needGridUpdate = value;
			}
		}
		public long lastMoveTick { get; set; }
		public bool NeedMoveFlag { get; set; }	// 오브젝트 이동중, 또는 Stop 상태 플래그
		public bool AlreadyMoved
		{
			get { return lastMoveTick == 0 ? false : true; }
		}

		//public LastMoveTickM(bool needMoveFlag)
		//{
		//    NeedMoveFlag = needMoveFlag;
		//}

		/// <summary>
		/// 리턴값이 -1 이면 업데이트 이후 따로 움직임이 없는 것임
		/// </summary>
		/// <returns></returns>
		public long GetElapsedTickAfterMoved(long curTick)
		{
			if (lastMoveTick == 0)
			{
				return 0;
			}
			return curTick - lastMoveTick;
		}

		// curTick == 0이면 초기화
		public void SetLastMoveTick(long curTick)
		{
			lastMoveTick = curTick;
		}

		public void ClearLastMoveTick()
		{
			lastMoveTick = 0;
		}

		public void Reset()
		{
			lastMoveTick = 0;
			NeedMoveFlag = false;
			NeedGridUpdate = false;
		}


	}


	public abstract class BaseGameObjM : IHasGameOid
	{
		public string name;
		public Entity entity;
				
		public TimeEventSchedulerM ExpireJobScheduler => ServerM.gTimeScheduler;

		HashM _hashM;
		HashM HashM  // 지연 생성
		{
			get
			{
				return LazyInitializer.EnsureInitialized(ref _hashM, () =>
				{
					return _hashM = new HashM(ExpireJobScheduler);
				});
				
			}
		}

		

		/// <summary>
		/// 해시값 설정
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public bool SetHash(string key, string value, int durationSec = -1)
		{
			return HashM.Set(key, value, durationSec);

		}

		/// <summary>
		/// 해시 지우기
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public bool RemoveHash(string key)
		{
			return HashM.Remove(key);
		}

		public bool HasHash(string key)
		{
			return HashM.Has(key);
		}

		/// <summary>
		/// 해시값 얻기
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public bool GetHash(string key, out string value)
		{
			return HashM.Get(key, out value);
		}

		/// <summary>
		/// 해시값을 얻어옴과 동시에 지우기
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public bool GetHashAndRemove(string key, out string value)
		{
			return HashM.GetAndRemove(key, out value);
		}




		/// <summary>
		/// name 초기화, oid 0으로 초기화
		/// </summary>
		virtual public void Clear()
		{
			name = null;
			_oid = 0;            // Oid 초기화 해야 다시 생성한다.
		}

		void MakeOid()
		{
			_oid = GlobalM.MakeGameOid();
		}

		protected long _oid = 0;
		virtual public long Oid
		{
			get
			{
				if (_oid == 0)
				{
					MakeOid();
				}
				return _oid;
			}
		}           // Object Id


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public ref PositionM GetPos()
		{
			ref var pos = ref entity.Get<PositionM>();
			return ref pos;
		}


		/// <summary>
		/// 내부 함수임 스크립트에서 절대 사용하지 말 것
		/// </summary>
		/// <param name="pos"></param>
		public void _SetPos(in PositionM pos)
		{
			ref var refPos = ref entity.Get<PositionM>();
			refPos.X = pos.X;
			refPos.Y = pos.Y;
			refPos.Z = pos.Z;
		}


		ref RotationM GetRotation()
		{
			ref var rot = ref entity.Get<RotationM>();
			return ref rot;

		}

		/// <summary>
		/// 주의 !! : 저장할 때 Normalize해서 저장함으로 Get한 값은 Normalize된 값임
		/// </summary>
		/// <returns></returns>
		public float GetAngle()
		{
			ref var rot = ref entity.Get<RotationM>();
			return rot.X;
		}


	}

	public struct ObjScriptM
	{
		public ScriptForGameObjM script { get; set; }

		public ObjScriptM(ScriptForGameObjM script)
		{
			this.script = script;
		}
	}



	/// <summary>
	/// Collision 처리등 기본 이벤트등이 필요한 
	/// </summary>
	public abstract class AbScriptableForGameObjM : BaseGameObjM
	{	

		public ScriptForGameObjM Script
		{
			get { return entity.Get<ObjScriptM>().script; }
		}
		public ref QuadPointColliderM Collider
		{
			get { return ref entity.Get<QuadPointColliderM>(); }
		}

		public ref ImgSizeM ImgSize
		{
			get { return ref entity.Get<ImgSizeM>(); }
		}


		/// <summary>
		/// 충돌시 컬리젼 메세지를 활성화 
		/// true값을 넘겨 트리거로 설정된 오브젝트는 OnTriggerEnter와 같은 Collision 메세지들이 불린다
		/// </summary>
		/// <param name="isTrigger">true면 OnTriggerEnter와 같은 Collision 메세지들이 불린다</param>
		public void SetTriggerObject(bool isTrigger)
		{
			Collider.IsTrigger = isTrigger;
		}
	}


	public struct MapScriptM
	{
		public AbScriptM script
		{
			get; set;
		}

		public MapScriptM(AbScriptM script)
		{
			this.script = script;
		}
	}

	/// <summary>
	/// 맵등 기본 스크립트만을 가진 Object에 쓰는 추상 클래스
	/// </summary>
	//public abstract class AbScriptableObjM
	//{
	//    public AbScriptM _script { get; set; }
	//    public abstract void _SetFieldScript(); // 이함수 구현시 _script 필드를 set해야 됨, 더블어 _script.Self도 설정해야 됨
	//    public abstract AbScriptableObjM GetScriptOwner();

	//    public void SetFieldScript()
	//    {
	//        _SetFieldScript();
	//        _script.Self = GetScriptOwner();
	//    }
	//}

	public abstract class MapObjM : IHasGameOid, IDisposable //AbScriptableObjM, IHasGameOid
	{
		protected long _oid;
		public long Oid { get => _oid; }           // Object Id

		static long _uinqueOid;
		HashM _hashM;

		public Entity mapEntity;

		private ProgressBarM _progressBar; // 프로그래시브바
		private bool _disposedValue;

		// Get할 때 한 번만 생성되는 프로퍼티
		public ProgressBarM ProgressBar
		{
			get
			{
				return LazyInitializer.EnsureInitialized(ref _progressBar, () =>
				{
					var bar = ProgressBarM.ProgressBarFactory.GetProgressBar();
					return bar;
				});
			}
		}


		// 반드시 entity 추가되어 있어야 함
		public TimeEventSchedulerM ExpireJobScheduler => ServerM.gTimeScheduler;

		protected void MakeOid()
		{
			_oid = Interlocked.Increment(ref _uinqueOid);
		}

		HashM HashM  // 지연 생성
		{
			get
			{
				return LazyInitializer.EnsureInitialized(ref _hashM, () =>
				{
					var hash = new HashM(ExpireJobScheduler);
					return hash;
				});				
			}
		}

		/// <summary>
		/// 해시값 설정
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public bool SetHash(string key, string value, int durationSec = -1)
		{
			return HashM.Set(key, value, durationSec);

		}

		/// <summary>
		/// 해시 지우기
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public bool RemoveHash(string key)
		{
			return HashM.Remove(key);
		}

		public bool HasHash(string key)
		{
			return HashM.Has(key);
		}

		/// <summary>
		/// 해시값 얻기
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public bool GetHash(string key, out string value)
		{
			return HashM.Get(key, out value);
		}

		/// <summary>
		/// 해시값을 얻어옴과 동시에 지우기
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		/// <returns></returns>
		public bool GetHashAndRemove(string key, out string value)
		{
			return HashM.GetAndRemove(key, out value);
		}

		abstract public void SendPacketToMapUsers(PACKET_TYPE pkType, byte[] data, long bExceptOid = -1);
		abstract public void WriteSendBufferToMapUsers(PACKET_TYPE pkType, byte[] data, long bExceptOid = -1);

		protected virtual void Dispose(bool disposing)
		{
			if (!_disposedValue)
			{
				if (disposing)
				{
					// TODO: 관리형 상태(관리형 개체)를 삭제합니다.
				}

				// TODO: 비관리형 리소스(비관리형 개체)를 해제하고 종료자를 재정의합니다.
				// TODO: 큰 필드를 null로 설정합니다.		
				
				//프로그래시바 풀에 반환
				ProgressBarM.ProgressBarFactory.ReturnToPool(_progressBar);				
				
				_disposedValue = true;
			}
		}

		// // TODO: 비관리형 리소스를 해제하는 코드가 'Dispose(bool disposing)'에 포함된 경우에만 종료자를 재정의합니다.
		// ~MapObjM()
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


		// 맵Obj 데이터 클리어
		public void Clear()
		{
			_oid = -1;
			_hashM = null;
			_progressBar = null;
		}
	}




}
