using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EcsServerLibM
{
    public class SrvClaFuncM
    {
        // 클라이언트에 공개
        static public void SimulPos(float angle, long elapsedTick, float speed, ref float x, ref float y)
        {

            double angleRadians = angle * Math.PI / 180.0;

            // x, y 방향으로의 이동량 계산
            double deltaX = Math.Cos(angleRadians) * speed * (double)elapsedTick / (double)Stopwatch.Frequency;
            double deltaY = Math.Sin(angleRadians) * speed * (double)elapsedTick / (double)Stopwatch.Frequency;

            // 새로운 위치 계산
            x = x + (float)deltaX;
            y = y + (float)deltaY;

        }
    }
}
