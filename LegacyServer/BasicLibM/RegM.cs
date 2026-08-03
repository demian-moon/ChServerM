namespace EcsServerLibM
{

#if NETFRAMEWORK

    /// <summary>
    /// 레지스트리 관련 클래스 
    /// </summary>
    public class RegM
    {
        REGM_KEY_SORT _eSort;        

        RegistryKey _rootKey;
        /// <summary>
        /// RegM 관련 enum값 : 레지스트리 
        /// </summary>
        public enum REGM_KEY_SORT { HKEY_USER, HKEY_MACHINE, HKEY_ROOT }

        public RegM(REGM_KEY_SORT eSort, string key)
        {
            _eSort = eSort;

            switch(eSort)
            {
                case REGM_KEY_SORT.HKEY_USER:
                    _rootKey = Registry.CurrentUser;
                    break;
                case REGM_KEY_SORT.HKEY_MACHINE:
                    _rootKey = Registry.LocalMachine;
                    break;
                case REGM_KEY_SORT.HKEY_ROOT:
                    _rootKey = Registry.ClassesRoot;
                    break;
                default:
                    Debug.Assert(false);
                    break;
            }

            if ( !string.IsNullOrEmpty(key) )   // 키가 null이면
            {
                RegistryKey tmKey = _rootKey.OpenSubKey(key, true); 
                if(tmKey == null )  // 해당 키가 없으면 생성
                {
                    _rootKey = _rootKey.CreateSubKey(key);
                }
                else
                {
                    _rootKey = tmKey;   // 있으면 루트 변경
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="dataKey"></param>
        /// <param name="val">설정할 값</param>
        /// <param name="registryValueKind">value 타입</param>
        public void SetValue(string dataKey, object val, RegistryValueKind registryValueKind)
        {            
            _rootKey.SetValue(dataKey, val, registryValueKind);
        }

        public object GetValue(string dataKey)
        {
           return _rootKey.GetValue(dataKey);
        }

        public void DeleteValue(string dataKey)
        {
            _rootKey.DeleteValue(dataKey);
        }

        public void DeleteKey(string key)
        {
            _rootKey.DeleteSubKey(key);
        }
    }
#endif
}