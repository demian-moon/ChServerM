using System;
using System.Diagnostics;
using System.Numerics;


namespace EcsServerLibM
{


	/// 위치 시뮬레이션 클래스 speed는 초당 움직이는 pixel을 의미한다
	public class MoveSimulationM
	{
		/// <summary>
		/// 이동 함수
		/// </summary>
		/// <param name="pos">현재 위치</param>
		/// <param name="rotation">0 ~ 360도 값을 갖는 앵글 (마이너스 각도값일 때도 제대로 동작한다)</param>
		/// <param name="speed">초당 pixel 스피드</param>
		/// <param name="elapsedTick">StopWatch를 사용한 tick</param>
		/// <returns></returns>
		static public void SimulPos(ref PositionM pos, in RotationM rotation, float speed, long elapsedTick)
		{

			double angleRadians = rotation.X * Math.PI / 180.0;

			// x, y 방향으로의 이동량 계산
			double deltaX = Math.Cos(angleRadians) * speed * (double)elapsedTick / (double)Stopwatch.Frequency;
			double deltaY = Math.Sin(angleRadians) * speed * (double)elapsedTick / (double)Stopwatch.Frequency;

			// 새로운 위치 계산
			pos.X = pos.X + (float)deltaX;
			pos.Y = pos.Y + (float)deltaY;						

		}
		

        /// <summary>
        /// 스크린 렉트 안에 있는 pos가 Simulation 결과 밖으로 나가면 렉트 경계선 좌표를 리턴
        /// </summary>
        /// <param name="rect">일반적은 Rect를 넘긴다(Right, Top 좌표는 포함되지 않는)</param>
        /// <param name="curPos"></param>
        /// <param name="rot"></param>
        /// <param name="speed"></param>
        /// <param name="elapsedTick"></param>
        /// <returns></returns>
        static public void SimulPosInRect(in RectM rect, ref PositionM curPos, in RotationM rotation, float speed, long elapsedTick)
		{
			SimulPos(ref curPos, rotation, speed, elapsedTick);
			if (rect.Contains(curPos))
			{
				return;
			}

			var newPos = Vector3.Clamp(curPos.V3, new Vector3(rect.Left, rect.Bottom, 0), new Vector3(rect.Right, rect.Top, 0));
			curPos.X = newPos.X;
			curPos.Y = newPos.Y;
		}

	}

	public static class LineIntersection
	{
		static float epsilon = 1e-5f;
		public static Vector3 FindIntersection(Vector3 p1, Vector3 p2, Vector3 q1, Vector3 q2)
		{

			// Check for division by zero
			float denominator = (p1.X - p2.X) * (q1.Y - q2.Y) - (p1.Y - p2.Y) * (q1.X - q2.X);
			if (denominator == 0)
			{
				Debug.Assert(false, "버그M: Denominator is zero"); // 평행 하다는 의미
			}

			// Calculate intersection point
			Vector3 intersect = new Vector3(
			   ((p1.X * p2.Y - p2.X * p1.Y) * (q1.X - q2.X) - (p1.X - p2.X) * (q1.X * q2.Y - q2.X * q1.Y)) / denominator,
				((p1.X * p2.Y - p2.X * p1.Y) * (q1.Y - q2.Y) - (p1.Y - p2.Y) * (q1.X * q2.Y - q2.X * q1.Y)) / denominator,
				0
			);

			//// Validate intersection point
			//if (!IsValidIntersection(intersect, p1, p2) || !IsValidIntersection(intersect, q1, q2)) // 교점이 없을 때
			//{
			//    Debug.Assert(false, "버그M: 접점이 없음");
			//}

			return intersect;
		}

#if NET7_0_OR_GREATER
		private static bool IsValidIntersection(Vector3 intersect, Vector3 p1, Vector3 p2)
		{
			Vector3 checkIntersect = new Vector3(
				float.Round(intersect.X, MidpointRounding.AwayFromZero),
				float.Round(intersect.Y, MidpointRounding.AwayFromZero),
				0
			);

			p1 = new Vector3(float.Round(p1.X, MidpointRounding.AwayFromZero), float.Round(p1.Y, MidpointRounding.AwayFromZero), 0);
			p2 = new Vector3(float.Round(p2.X, MidpointRounding.AwayFromZero), float.Round(p2.Y, MidpointRounding.AwayFromZero), 0);

			return checkIntersect.X >= Math.Min(p1.X, p2.X) &&
			checkIntersect.X <= Math.Max(p1.X, p2.X) &&
			checkIntersect.Y >= Math.Min(p1.Y, p2.Y) &&
			checkIntersect.Y <= Math.Max(p1.Y, p2.Y);

		}


#else
        private static bool IsValidIntersection(Vector3 intersect, Vector3 p1, Vector3 p2)
        {
            Vector3 checkIntersect = new Vector3(
                (float)Math.Round(intersect.X, MidpointRounding.AwayFromZero),
                (float)Math.Round(intersect.Y, MidpointRounding.AwayFromZero),
                0
            );

            p1 = new Vector3((float)Math.Round(p1.X, MidpointRounding.AwayFromZero), (float)Math.Round(p1.Y, MidpointRounding.AwayFromZero), 0);
            p2 = new Vector3((float)Math.Round(p2.X, MidpointRounding.AwayFromZero), (float)Math.Round(p2.Y, MidpointRounding.AwayFromZero), 0);

            return checkIntersect.X >= Math.Min(p1.X, p2.X) &&
            checkIntersect.X <= Math.Max(p1.X, p2.X) &&
            checkIntersect.Y >= Math.Min(p1.Y, p2.Y) &&
            checkIntersect.Y <= Math.Max(p1.Y, p2.Y);

        }
#endif

		//public class Point
		//{
		//    public float X { get; private set; }
		//    public float Y { get; private set; }

		//    public Point(float x, float y)
		//    {
		//        X = x;
		//        Y = y;
		//    }

		//    public void Move(float deltaX, float deltaY, Rect rect)
		//    {
		//        float newX = X + deltaX;
		//        float newY = Y + deltaY;

		//        if (newX >= rect.X && newX <= rect.X + rect.Width && newY >= rect.Y && newY <= rect.Y + rect.Height)
		//        {
		//            X = newX;
		//            Y = newY;
		//            return;
		//        }

		//        // Calculate intersection with left or right boundary
		//        if (deltaX != 0)
		//        {
		//            float tX1 = (rect.X - X) / deltaX;
		//            float tX2 = (rect.X + rect.Width - X) / deltaX;
		//            float tX = Math.Min(tX1, tX2);

		//            float tempX = X + deltaX * tX;
		//            float tempY = Y + deltaY * tX;

		//            if (tempX >= rect.X && tempX <= rect.X + rect.Width && tempY >= rect.Y && tempY <= rect.Y + rect.Height)
		//            {
		//                X = tempX;
		//                Y = tempY;
		//                return;
		//            }
		//        }

		//        // Calculate intersection with top or bottom boundary
		//        if (deltaY != 0)
		//        {
		//            float tY1 = (rect.Y - Y) / deltaY;
		//            float tY2 = (rect.Y + rect.Height - Y) / deltaY;
		//            float tY = Math.Min(tY1, tY2);

		//            float tempX = X + deltaX * tY;
		//            float tempY = Y + deltaY * tY;

		//            if (tempX >= rect.X && tempX <= rect.X + rect.Width && tempY >= rect.Y && tempY <= rect.Y + rect.Height)
		//            {
		//                X = tempX;
		//                Y = tempY;
		//                return;
		//            }
		//        }

		//        // Final adjustment if none of the above worked
		//        if (newX < rect.X) X = rect.X;
		//        else if (newX > rect.X + rect.Width) X = rect.X + rect.Width;

		//        if (newY < rect.Y) Y = rect.Y;
		//        else if (newY > rect.Y + rect.Height) Y = rect.Y + rect.Height;
		//    }
		//}




	}
}



