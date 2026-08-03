using UnityEngine;
using System;
using System.Collections;



/// <summary>
/// 클래스 템플릿으로 Catmull-rom Spline을 구현한다
/// 전체 Path에 대한 ease funtion을 자연스럽게 구현하기 위해서 이다
/// 
/// 전체 _totPathLength를 기준으로 int   
/// 
/// </summary>


public class CatmullRomSpline {

    Vector3 [] _pts;
    int _cntPts;
    

    float [] _arrLength;
    float _totPathLength;
    bool _addDummyPath;
	
  
    public CatmullRomSpline(Vector3[] path, bool addDummyPath = false)
    {
        _addDummyPath = addDummyPath;
        if(addDummyPath == true)
        {
            path = PathControlPointGenerator(path);           

        }

        _cntPts = path.Length;
        if (_cntPts < 4)
        {
            Debug.Log("Error : Path Length must be bigger than 4");
            return;
        }
        _pts = path;


        _arrLength = new float[_cntPts - 3];
        CalculateLength();

    }

    /* Interpolation in Path */
    public Vector3 Interp(float t)
    {
        float posLength = _totPathLength * t;
                
        float u = 0;
        int currIdx = GetPtsIndex(posLength, out u);
        //Debug.Log("posSeg: " + posLength.ToString() + " : curIdx +" + currIdx.ToString() + " u : " + u.ToString() );

        Vector3 a = _pts[currIdx];
        Vector3 b = _pts[currIdx + 1];
        Vector3 c = _pts[currIdx + 2];
        Vector3 d = _pts[currIdx + 3];


        float u2 = u * u;
        float u3 = u2 * u;

        return .5f * ((-a + 3f * b - 3f * c + d) * (u3) + (2f * a - 5f * b + 4f * c - d) * (u2) + (-a + c) * u + 2f * b);

    }

    /// <summary>
    /// 특정 Flight의 경로를 그 주변 Flight들의 현재 위치를 고려해서 
    /// 마지막 위치는 동일 하도록 새로운 경로를 만든다
    /// 클래스 생성시 주어진 path를 가지고 segment개수 만큼의 경로를 만든다
    /// </summary>
    /// <param name="curPos">경로를 만들고자 하는 Flight의 현재 위치</param>
    /// <param name="segment"></param>
    /// <returns></returns>
    /// 
    public Vector3[] MakePath (Vector3 curPos, int segment)
    {
        if (segment == 1)
        {
            Debug.LogError("Error : Segment is bigger than 1");
            return null;
        }

        Vector3 standardPts = Vector3.zero;
        if (_addDummyPath == false)
        {
            standardPts = _pts[0];
        }
        else
        {
            standardPts = _pts[1];
        }

        Vector3[] rtn = new Vector3[segment];
        float xDif = curPos.x - standardPts.x;
        float yDif = curPos.y - standardPts.y;


        for(int i=0; i<segment; i++)
        {
            float t = (float)i / (float)(segment - 1);

            float xModify = xDif * (1f - t*0.3f);  /* 모이는 정도의 가중치 0.3f*/
            float yModify = yDif * (1f - t*0.3f);

            rtn[i] = Interp(t) + new Vector3(xModify, yModify, 0f);
        }

        return rtn;
    }

    public float GetTotalDistance()
    {
        return _totPathLength;
    }   

    private void CalculateLength()
    {
        int idx = 0;
        for(int i=0; i<_cntPts-3; i++)
        {
            //Vector3 a = _path[i];
            Vector3 b = _pts[i + 1];
            Vector3 c = _pts[i + 2];
            //Vector3 d = _path[i + 3];

            float distance = Vector3.Distance(b, c);
           // Debug.Log(distance.ToString());


            _totPathLength += distance;
            _arrLength[idx] = _totPathLength;            
            idx++;
        }        
    }

    /* u : 0 ~ 1사이의 값 */
    /*   Length로 path의 Index를 얻고 u값을 계산해서 리턴 */
    int GetPtsIndex(float posLength, out float u)
    {
        int ptsIndex = 0;
        if(posLength == _totPathLength)
        {
            u = 1;
            return _arrLength.Length - 1;
        }
        
        for(int i=0; i<_arrLength.Length; i++)
        {
            if (_arrLength[i] > posLength)
            {
                ptsIndex = i;                
                break;
            }
        }       
                
        float totCurLength = 0;
        float totPreLength = 0;
        if (ptsIndex == 0)
        {            
            totPreLength = 0;            
        }
        else
        {            
            totPreLength = _arrLength[ptsIndex - 1];
        }

        totCurLength = _arrLength[ptsIndex];

        if (totCurLength != totPreLength)
        {
            u = (posLength - totPreLength) / (totCurLength - totPreLength);
        }
        else
        {
            u = 0;
        }
        return ptsIndex;
    }

    /* CatmullRom Spline을 위해서 맨앞과 맨뒤 더미 패스를 생성하는 루틴 */
    public static Vector3[] PathControlPointGenerator(Vector3[] path)
    {
        Vector3[] suppliedPath;
        Vector3[] vector3s;

        //create and store path points:
        suppliedPath = path;

        //populate calculate path;
        int offset = 2;
        vector3s = new Vector3[suppliedPath.Length + offset];
        Array.Copy(suppliedPath, 0, vector3s, 1, suppliedPath.Length);

        //populate start and end control points:
        //vector3s[0] = vector3s[1] - vector3s[2];
        vector3s[0] = vector3s[1] + (vector3s[1] - vector3s[2]);
        vector3s[vector3s.Length - 1] = vector3s[vector3s.Length - 2] + (vector3s[vector3s.Length - 2] - vector3s[vector3s.Length - 3]);

        //is this a closed, continuous loop? yes? well then so let's make a continuous Catmull-Rom spline!
        if (vector3s[1] == vector3s[vector3s.Length - 2])
        {
            Vector3[] tmpLoopSpline = new Vector3[vector3s.Length];
            Array.Copy(vector3s, tmpLoopSpline, vector3s.Length);
            tmpLoopSpline[0] = tmpLoopSpline[tmpLoopSpline.Length - 3];
            tmpLoopSpline[tmpLoopSpline.Length - 1] = tmpLoopSpline[2];
            vector3s = new Vector3[tmpLoopSpline.Length];
            Array.Copy(tmpLoopSpline, vector3s, tmpLoopSpline.Length);
        }

        return (vector3s);
    }

}

