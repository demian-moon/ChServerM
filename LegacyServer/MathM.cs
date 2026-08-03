using System;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace EcsServerLibM
{
	public static class MathM
	{
		// 라디안 상수값        
		public static readonly double Deg2Rad = Math.PI / 180.0f;

		/// <summary>
		/// 소스 위치에서 타겟 위치의 각도(-180 ~ 180도 사이)
		/// </summary>
		/// <param name="sourcePos"></param>
		/// <param name="targetPos"></param>
		/// <returns></returns>
		static public double GetAngleDegreesToTargetPos(in Vector3 sourcePos, in Vector3 targetPos)
		{
			Vector3 direction = targetPos - sourcePos; // Calculate direction vector

			double angleRadians = Math.Atan2(direction.Y, direction.X); // Calculate angle in radians Atan2 범위가 (-180 ~ 180도 사이이다)
			double angleDegrees = angleRadians * (180.0f / Math.PI); // Convert angle to degrees

			return angleDegrees;
		}

		static public double GetDistanceBetweenPos(in Vector3 sourcePos, in Vector3 targetPos)
		{
			var direction = targetPos - sourcePos;
			return Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
		}
		static public double GetDistanceBetweenPos(in PositionM sourcePos, in PositionM targetPos)
		{
			var direction = targetPos - sourcePos;
			return Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
		}

		/// <summary>
		/// 특정 위치를 기준으로 특정 각도에서 distance만큼 떨어진 위치를 구하는 함수
		/// </summary>
		/// <param name="posM"></param>
		/// <param name="angle"></param>
		/// <param name="distance"></param>
		/// <returns></returns>
		static public PositionM GetAnglePosAtDistance(in PositionM posM, float angle, float distance)
		{
			var angRadian = angle * MathM.Deg2Rad;
			var x = posM.X + (Math.Cos(angRadian) * distance);
			var y = posM.Y + (Math.Sin(angRadian) * distance);

			return new PositionM((float)x, (float)y, 0);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static public float NormalizeAngle(float angle)
		{
			angle %= 360;
			return angle < 0 ? angle + 360 : angle;
		}

		/// <summary>
		/// point가 3개의 점으로 구성된 삼각형 안에 있는지 검사
		/// </summary>
		/// <param name="point"></param>
		/// <param name="a"></param>
		/// <param name="b"></param>
		/// <param name="c"></param>
		/// <returns></returns>
		static public bool IsPointInTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
		{
			Vector2 v0 = c - a;
			Vector2 v1 = b - a;
			Vector2 v2 = point - a;

			float dot00 = Vector2.Dot(v0, v0);
			float dot01 = Vector2.Dot(v0, v1);
			float dot02 = Vector2.Dot(v0, v2);
			float dot11 = Vector2.Dot(v1, v1);
			float dot12 = Vector2.Dot(v1, v2);

			float invDenom = 1 / (dot00 * dot11 - dot01 * dot01);
			float u = (dot11 * dot02 - dot01 * dot12) * invDenom;
			float v = (dot00 * dot12 - dot01 * dot02) * invDenom;

			return (u >= 0) && (v >= 0) && (u + v < 1);
		}

        /// <summary>
        /// angle만큼 Rotation 했을 때를 가정해 width, height 사이즈 리턴
        /// </summary>
        /// <param name="angle"></param>
        /// <returns></returns>
        static public (float Width, float Height) GetBoundingSizeAfterRotation(float angle, float width, float height)
        {
            // 노말라이즈 
            angle = MathM.NormalizeAngle(angle);

            // 각도를 라디안으로 변환
            float radian = angle * MathF.PI / 180f;
            float cos = MathF.Cos(radian);
            float sin = MathF.Sin(radian);

            // 회전된 폭과 높이 계산 (바운딩 박스)
            float rotatedWidth = MathF.Abs(width * cos) + MathF.Abs(height * sin);
            float rotatedHeight = MathF.Abs(width * sin) + MathF.Abs(height * cos);

            return (rotatedWidth, rotatedHeight);
        }

    }
	/// <summary>
	/// 모튼 코드 함수
	/// </summary>
	public static class MortonCodeM
	{
		// 2D 좌표 (x, y)를 모튼 코드로 인코딩하는 메서드
		static public UInt32 EncodeMorton2(UInt32 x, UInt32 y)
		{
			return (Part1By1(y) << 1) + Part1By1(x); // y의 부분 비트를 왼쪽으로 이동하고 x의 비트를 더하여 모튼 코드 생성
		}


		/// <summary>
		/// 1D 비트를 교차 삽입하여 모튼 코드를 만드는 데 사용되는 메서드 (Z-order curve)
		/// 좌표 x값과, 좌표 y값을 번갈아 가며 비트로 채우기위함
		/// </summary>
		/// <param name="x"></param>
		/// <returns></returns>
		//
		/* 
        x &= 0x0000ffff:
        → 0b00000000000000001111111111111111 (상위 16비트는 0이 됨)

        x = (x ^ (x << 8)) & 0x00ff00ff:
        → 0b00000000111111110000000011111111 (두 개의 8비트 그룹으로 나누어 퍼뜨림)

        x = (x ^ (x << 4)) & 0x0f0f0f0f:
        → 0b00001111000011110000111100001111 (각 8비트 그룹을 4비트씩 퍼뜨림)

        x = (x ^ (x << 2)) & 0x33333333:
        → 0b00110011001100110011001100110011 (각 4비트를 2비트씩 퍼뜨림)

        x = (x ^ (x << 1)) & 0x55555555:
        → 0b01010101010101010101010101010101 (각 2비트를 1비트씩 퍼뜨림) */
		static public UInt32 Part1By1(UInt32 x)
		{
			x &= 0x0000ffff; // x의 하위 16비트를 남김
			x = (x ^ (x << 8)) & 0x00ff00ff; // x의 비트를 8비트 왼쪽으로 이동하여 교차 삽입
			x = (x ^ (x << 4)) & 0x0f0f0f0f; // x의 비트를 4비트 왼쪽으로 이동하여 교차 삽입
			x = (x ^ (x << 2)) & 0x33333333; // x의 비트를 2비트 왼쪽으로 이동하여 교차 삽입
			x = (x ^ (x << 1)) & 0x55555555; // x의 비트를 1비트 왼쪽으로 이동하여 교차 삽입
			return x; // 최종적으로 교차 삽입된 비트 반환
		}

		// 2D 포인트를 모튼 인덱스로 변환하는 메서드
		static public UInt32 MortonIndex2(PointF pointF, float minX, float minY, float width, float height)
		{
			// 포인트를 새로운 원점으로 이동 (x, y 최소값을 빼면 0, 0에서 시작하는 새로운 좌표)
			pointF = new PointF(pointF.X - minX, pointF.Y - minY);

			// float 좌표 값을 그냥 사용하면 모튼 코드가 중복 될 수 있으므로 
			// 0 ~ UInt16.MaxValue 사이의 비율로 x 좌표를 정규화하여 pX, pY 계산
			var pX = (UInt32)(UInt16.MaxValue * pointF.X / width);
			var pY = (UInt32)(UInt16.MaxValue * pointF.Y / height);

			// pX와 pY를 사용하여 모튼 인덱스 반환
			return EncodeMorton2(pX, pY);
		}
	}

}
