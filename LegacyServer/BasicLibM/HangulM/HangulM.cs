using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;


namespace HangulM
{
    class HangulM
    {
        /* 한글 */
        /*
         *    !! 주의 사항 !! 한글 코드값 ㄱ - ㅎ은 중간에 종성으로 쓰이는 ㄳ같은 코드가 섞여 있음 
            - 초성: ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ(19가지) : 
            - 중성: ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ(21가지)
            - 종성 : 없음, ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ(28가지)
        */




        /// <summary>
        /// 종성으로 쓰이는 초성에 따른 조합개수 - 예) 'ㄱ'의 경우 ㄲ ㄳ이 있으므로 2개 
        /// key : 초성, value : 해당 초성으로 변경될 수 있는 종성의 개수
        /// 
        /// </summary>
        /// 4520 : 종성 'ㄱ'에 해당하는 코드값임 - Normalize() 분해 했을 때 실제 종성값이 들어가므로 
        static public Dictionary<char, int> DIC_CNT_CHO_JONG_VARIETY = new Dictionary<char, int>
                                                        { {/*'ㄱ'*/(char)4520, 2 }, { /*'ㄴ'*/ (char)4523 , 2 }, { /*'ㄹ'*/(char)4527, 7 }, { /*'ㅂ'*/(char)4536, 1 }, { /*'ㅅ'*/(char)4538, 1 } };

        /* 완성 한글의 첫글자 '가'의 코드 */
        static readonly int FIRST_HANGUL_CODE = 44032;
        /* 완성 한글의 마지막 글자 '힣'의 코드 */
        static readonly int LAST_HANGUL_CODE = 55203;


        /* '가' 부터 '깋' 까지 글자 개수 : 중성 (21) * 종성 (28) = 588 */
        static readonly int CNT_ALL_HANGUL_IN_CHO = 588;
        static readonly int CNT_ALL_JONG = 28;


        //const int FIRST_CHO_CODE = 12593, 12622

        static readonly char[] CHO = { 'ㄱ', 'ㄲ', 'ㄴ', 'ㄷ', 'ㄸ', 'ㄹ', 'ㅁ', 'ㅂ', 'ㅃ', 'ㅅ', 'ㅆ',
                                    'ㅇ', 'ㅈ', 'ㅉ', 'ㅊ','ㅋ','ㅌ', 'ㅍ', 'ㅎ' };

        static readonly char[] CHO_FIRST_HANGUL = { '가', '까', '나', '다', '따', '라', '마', '바', '빠', '사', '싸',
                                                    '아', '자', '짜', '차','카','타', '파', '하' };

        /* 가 - 하 코드값*/
        static readonly int[] CHO_FIRST_HANGUL_CODE = {44032,44620,45208,45796,46384,46972,47560,48148,48736,49324,49912,
                               50500,51088,51676,52264,52852,53440,54028,54616};


        public enum HANSTATE { CHO, JUNG, JONG, ERROR };

        /// <summary>
        /// 하나의 한글 문자를 매개변수로 받고 이 한글이 초성만 있는지 중성까지 있는지 종성까지 있는지 검사하는 함수
        /// </summary>
        /// <param name="hangul"></param>
        /// <returns></returns>
        static public HANSTATE CheckHangulState(char hangul)
        {
            string nfd = hangul.ToString().Normalize(NormalizationForm.FormD);
            int len = nfd.Length;
            HANSTATE state;
            if (len == 1)
            {
                state = HANSTATE.CHO;
            }
            else if (len == 2)
            {
                state = HANSTATE.JUNG;
            }
            else if (len == 3)
            {
                state = HANSTATE.JONG;
            }
            else
            {
                state = HANSTATE.ERROR;
            }

            return state;
        }

        /// <summary>
        /// 초성인지 검사해서 맞으면 초성중 몇번째인지 index를 리턴
        /// </summary>
        /// <param name="chr"></param>
        /// <returns></returns>
        static public int CheckCho(char chr)
        {
            for (int i = 0; i < CHO.Length; ++i)
            {
                if (chr == CHO[i])
                    return i;
            }
            return -1;
        }

        static public bool IsHangul(char chr)
        {
            int idxCho = CheckCho(chr);

            if (idxCho == -1) // 초성이 아니면서 
            {
                if (chr < FIRST_HANGUL_CODE || chr > LAST_HANGUL_CODE)
                {
                    return false;
                }
            }

            return true;
        }


        /// <summary>
        /// 초성을 초성의 첫 한글로 변환하는 함수
        /// </summary>
        /// <param name="cho">한글초성</param>
        /// <returns></returns>
        static public char ConvertChoToFirstHangul(char cho)
        {
            int idxCho = CheckCho(cho);
            if (idxCho == -1)
                throw new ArgumentNullException();

            return CHO_FIRST_HANGUL[idxCho];
        }
        
               
        /// <summary>
        /// 한글자(초성포함)을 가지고 검색을 위한 정규식 패턴을 만드는 함수
        /// </summary>
        /// <param name="hangul"></param>
        /// <returns>정규식 패턴</returns>
        static protected string _MakeRegexWithHangulForSearch(char hangul)
        {
            /* 한글인지 검사 */

            if( IsHangul(hangul) == false)
                    throw new ArgumentNullException();
                            

            HANSTATE state_hangul = CheckHangulState(hangul);
            string regexPattern = "";

            char cvtHangulChar;

            if (state_hangul == HANSTATE.CHO) // 초성일 때 
            {
                cvtHangulChar = ConvertChoToFirstHangul(hangul);
                regexPattern = string.Format("[{0}-{1}]", cvtHangulChar, (char)(cvtHangulChar + CNT_ALL_HANGUL_IN_CHO - 1));

            }
            else // 완성형 글자일 때
            {
                string hangulEnd = "";
                // 각 초성의 첫글자 인지 검사
                int checkChoFirstHangul = (hangul - FIRST_HANGUL_CODE) % CNT_ALL_HANGUL_IN_CHO;

                if (checkChoFirstHangul == 0) // 각 초성의 첫글자 -> '가', '나' 등
                {
                    hangulEnd = ((char)(hangul + CNT_ALL_JONG - 1)).ToString(); // 현재글자에서 종성모두 포함하는 마지막 글자 얻기 ( '가'로 '갛' 을 얻음)
                }
                else
                {
                    if (state_hangul == HANSTATE.JUNG) // 종성이 없으면 : '매'-'맿'
                    {
                        hangulEnd = ((char)(hangul + CNT_ALL_JONG - 1)).ToString(); // 현재글자에서 종성모두 포함하는 글자까지
                    }
                    else if (state_hangul == HANSTATE.JONG) // 종성이 있으면 
                    {
                        /* nfd[2] 는 종성을 의미 */
                        /* DIC_CNT_CHO_JONG_VARIETY는 여러가지 종성으로 변경이 가능한 종성 Dictionary : ex) 'ㄱ' -> ㄲ, ㄳ */

                        string nfd = hangul.ToString().Normalize(NormalizationForm.FormD);
                        int cntVariety;
                        cntVariety = DIC_CNT_CHO_JONG_VARIETY.TryGetValue(nfd[2], out cntVariety) ? cntVariety : 0;

                        if (cntVariety == 0) // 이미 변경될 여지가 없는 완성형 한글자임
                        {
                            return hangul.ToString();
                        }

                        hangulEnd = ((char)(hangul + cntVariety)).ToString();
                    }
                }

                regexPattern = string.Format("[{0}-{1}]", hangul, hangulEnd); // 
            }


            return regexPattern;
        }


        /// <summary>
        /// 하나의 글자(utf-16)를 받고 그 글자로 만들수 있는 모든 한글을 검색 가능하도록 - 정규식 패턴 문자열을 리턴 해주는 함수
        /// </summary>
        /// <param name="letter"></param>
        /// <returns></returns>
        static public string MakeRegexForSearch(char letter)
        {
            string regexPattern = "";
            if (IsHangul(letter) )                
            {
                regexPattern = _MakeRegexWithHangulForSearch(letter);
            }
            else // 한글 아니면
            {
                try
                {
                    regexPattern = Regex.Escape(letter.ToString());
                }
                catch  // escape 문자 아니면
                {
                    regexPattern = letter.ToString();  // 일반 영문자 
                }
            }

            return regexPattern;

        }
    }
}