using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using System.Xml.XPath;

namespace WeatherAlertPushClient {
    class WeatherAlertPushClient {

        //事件列舉
        private enum EVENT_TYPE {
            PROC_SUCCESS_FINISH,      //程序成功結束
            CAP_FILE_LOST,       //cap 檔案遺失
            INPUT_PARAM_ERROR,        //傳入參數錯誤
            CAP_FILE_EMPTY,       //cap 檔案內容為空
            CAP_HANDLE_ERROR,     //cap 檔案處理錯誤
            TCP_SOCKET_ERROR,     //tcp socket 連線錯誤
            SERVER_HANDLE_ERROR,      //tcp socket server 處理錯誤
            PUSHED_TO_SERVER,       //已將資料發送資料至 tcp socket server
            PUSH_TYPE_ERROR     //要發送的資料類型錯誤
        };

        //發送資料類型範本
        private static Dictionary<string, string> PUSH_TYPE_TEMPLATE = new Dictionary<string, string>() {
            {"-eqa", "earthquake ## {0} ## {1}，{2} ## {3}"}      //發送地震速報文字範本
        };

        //正式模式下 status 元素值集合
        private static readonly List<string> FORMAL_STATUS_LIST = new List<string>() { "Actual" };

        //正式模式下 msgType 元素值集合
        private static readonly List<string> FORMAL_MSGTYPE_LIST = new List<string>() { "Alert", "Update" };

        //測試用參數
        private static readonly string TEST_PARAM = "-test";

        //發送資料完整結束點標記
        private static readonly string PUSH_END_POINT = "#%PUSH_END_POINT#%";

        //執行位置目錄
        private static readonly string EXEC_DIRECTORY = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        //儲存傳入參數集合
        private static List<string> paramsList = new List<string>();

        //是否為執行測試
        private static bool isExecTest = false;

        //程序唯一識別碼
        private static string sequence = string.Empty;

        static void Main(string[] args) {

            //產生亂數作為每次執行程序唯一識別碼
            makeRand(0, 9, 10).ForEach(num => sequence += num.ToString());

            if (args.Length < 2) {
                writeEventLog(EVENT_TYPE.INPUT_PARAM_ERROR,
                              "傳入執行檔所需參數遺失 (格式：\"發送類型\" \"CAP檔案名稱\" \"-test[若有此參數則為測試模式]\")",
                              true);
                return;
            }

            //取得傳入參數
            for (int i = 0; i < args.Length; i++) {

                //目前此版本第一個參數僅支援地震速報
                if (i == 0) {
                    if (!PUSH_TYPE_TEMPLATE.ContainsKey(args[i].ToLowerInvariant())) {
                        writeEventLog(EVENT_TYPE.PUSH_TYPE_ERROR,
                                      string.Format("您的發送類型參數錯誤 ({0})，目前此版本第一個參數僅支援 \"-eqa\" (地震速報)", args[i]),
                                      true);
                        return;
                    }
                }

                //若第三個參數為： -test 則列為測試執行
                if (i == 2) {
                    if (args[i].ToLowerInvariant().Equals(TEST_PARAM)) {        //測試執行
                        isExecTest = true;
                    } else {        //有第二個參數但不是 "-test"
                        writeEventLog(EVENT_TYPE.INPUT_PARAM_ERROR,
                                      string.Format("您傳入執行檔的第三個參數錯誤 ({0})，目前此版本第三個參數僅支援 \"-test\" (測試模式)", args[i]),
                                      true);
                        return;
                    }
                }

                paramsList.Add(args[i].Trim());
            }

            string capContent = getCapContent();
            if (!string.IsNullOrEmpty(capContent)) {
                string pushContent = string.Empty;

                //解析 cap 檔案
                try {
                    pushContent = processingCapFile(capContent);
                } catch (Exception ex) {
                    writeEventLog(EVENT_TYPE.CAP_HANDLE_ERROR, string.Format("解析速報 CAP 檔案發生錯誤 ({0})", ex.Message), false);
                    return;
                }

                //TCP Socket 連線
                try {
                    switch (paramsList[0].ToLowerInvariant()) {
                        case "-eqa":        //地震速報
                            processingTcpSocket(pushContent, getAppSettings("EARTHQUAKE_USE_VERSION"));
                            break;
                    }

                } catch (Exception ex) {
                    writeEventLog(EVENT_TYPE.TCP_SOCKET_ERROR, string.Format("發送速報資料發生 TCP Socket 連線錯誤 ({0})", ex.Message), false);
                    return;
                }
            }
        }

        /// <summary>
        /// 讀取 cap 檔案內容
        /// </summary>
        /// <returns>string</returns>
        private static string getCapContent() {
            string result = string.Empty;
            string capFilePath = string.Empty;
            string pushTypeKey = paramsList[0].ToLowerInvariant();

            //判斷要處理的 cap 檔類型
            switch (pushTypeKey) {
                case "-eqa":        //地震速報

                    //以目前執行位置目錄取得 cap 檔位置
                    capFilePath = string.Format(@"{0}\{1}\{2}",
                        //執行目錄
                        EXEC_DIRECTORY,
                        //cap 檔案目錄
                        ((!isExecTest) ? getAppSettings("EARTHQUAKE_CAP_FOLDER") : getAppSettings("EARTHQUAKE_CAP_FOLDER_TEST")),
                        //cap 檔名
                        paramsList[1]);
                    break;
            }

            //檢查檔案是否存在
            if (File.Exists(capFilePath)) {
                result = File.ReadAllText(capFilePath, Encoding.UTF8);
                result = result.Replace("\r\n", string.Empty);

                //檢查檔案內容
                if (string.IsNullOrEmpty(result)) {
                    writeEventLog(EVENT_TYPE.CAP_FILE_EMPTY, string.Format("速報 CAP 檔案內容為空白 ({0})", capFilePath), false);
                }

            } else {
                writeEventLog(EVENT_TYPE.CAP_FILE_LOST, string.Format("指定的速報 CAP 檔案未找到 ({0})", capFilePath), false);
            }

            return result;
        }

        /// <summary>
        /// 事件紀錄 log 檔處理
        /// </summary>
        /// <param name="eventType">事件類型</param>
        /// <param name="message">事件訊息</param>
        /// <param name="isSystemLog">是否為系統事件紀錄</param>
        private static void writeEventLog(EVENT_TYPE eventType, string message, bool isSystemLog) {
            string logDate = DateTime.Now.ToString("yyyy-MM-dd");
            string logTime = DateTime.Now.ToString("HH:mm:ss");
            string logFolder = string.Empty;

            //判斷要存放 log 檔案資料夾類型
            if (isSystemLog) {        //系統 log
                logFolder = getAppSettings("SYSTEM_LOG_FOLDER");
            } else {        //類型 log
                string pushTypeKey = paramsList[0].ToLowerInvariant();
                switch (pushTypeKey) {
                    case "-eqa":        //地震速報
                        logFolder = ((!isExecTest) ? getAppSettings("EARTHQUAKE_LOG_FOLDER") : getAppSettings("EARTHQUAKE_LOG_FOLDER_TEST"));
                        break;
                }
            }

            //log 存放位置
            string logDirectory = string.Format(@"{0}\{1}\", EXEC_DIRECTORY, logFolder);

            if (!Directory.Exists(logDirectory)) {
                Directory.CreateDirectory(logDirectory);
            }

            //寫入 log
            string logMessage = string.Format(@"{0} - {1} - {2} - {3} - {4}{5}",
                                                sequence,
                                                logTime,
                                                eventType.ToString(),
                                                message,
                                                ((isSystemLog) ? string.Empty : paramsList[1]),
                                                Environment.NewLine);

            createOrAppendFile(
                        logDirectory,
                        string.Format("{0}_{1}", logDate, getAppSettings("LOG_FILE_NAME")),
                        new List<string>() { logMessage });

            Console.WriteLine(logMessage);
        }

        /// <summary>
        /// 解析 cap 檔案轉為要發送的內容
        /// </summary>
        /// <param name="content">cap 內容</param>
        /// <returns>string</returns>
        private static string processingCapFile(string content) {
            string result = string.Empty;
            string pushTypeKey = paramsList[0].ToLowerInvariant();

            try {
                switch (pushTypeKey) {
                    case "-eqa":        //地震速報
                        XDocument capXdoc = XDocument.Parse(content, LoadOptions.None);

                        //執行正式發送模式需檢查 status、msgType 元素值
                        if (!isExecTest) {
                            string status = capXdoc.XPathSelectElement("//*[local-name(.)='alert']/*[local-name(.)='status']").Value;
                            string msgType = capXdoc.XPathSelectElement("//*[local-name(.)='alert']/*[local-name(.)='msgType']").Value;

                            if ((!FORMAL_STATUS_LIST.Contains(status)) || (!FORMAL_MSGTYPE_LIST.Contains(msgType))) {
                                throw new Exception(string.Format("正式發送模式下，CAP 元素內容 //alert/status 必須是 \"{0}\"，//alert/msgType 必須是 \"{1}\"",
                                                                   concatListString(FORMAL_STATUS_LIST, ","),
                                                                   concatListString(FORMAL_MSGTYPE_LIST, ",")));
                            }
                        }

                        //20160719，新版地震速報 cap 檔案
                        //info element
                        XElement infoXelt = capXdoc.XPathSelectElement("//*[local-name(.)='alert']/*[local-name(.)='info']");
                        string xpath = string.Empty;

                        //發生時間
                        xpath = @"//*[local-name(.)='parameter']
                                   /*[local-name(.)='valueName' and .='EventOriginTime']
                                   /following-sibling::*[local-name(.)='value']";
                        string originTime = infoXelt.XPathSelectElement(xpath).Value;
                        if (string.IsNullOrEmpty(originTime)) {
                            throw new Exception("無法取得 CAP 元素內容 (EventOriginTime)");
                        }

                        //發生地區
                        xpath = @"//*[local-name(.)='parameter']
                                   /*[local-name(.)='valueName' and .='EventLocationName']
                                   /following-sibling::*[local-name(.)='value']";
                        string locationName = infoXelt.XPathSelectElement(xpath).Value;
                        if (string.IsNullOrEmpty(locationName)) {
                            throw new Exception("無法取得 CAP 元素內容 (EventLocationName)");
                        }

                        //警告條件
                        xpath = @"//*[local-name(.)='parameter']
                                   /*[local-name(.)='valueName' and .='EventWarningCriterion']
                                   /following-sibling::*[local-name(.)='value']";
                        string warningCriterion = infoXelt.XPathSelectElement(xpath).Value;
                        if (string.IsNullOrEmpty(warningCriterion)) {
                            throw new Exception("無法取得 CAP 元素內容 (EventWarningCriterion)");
                        }

                        //警告區域
                        xpath = @"//*[local-name(.)='parameter']
                                   /*[local-name(.)='valueName' and .='EventWarningArea']
                                   /following-sibling::*[local-name(.)='value']";
                        string warningArea = infoXelt.XPathSelectElement(xpath).Value.Replace("，", " ").Replace("、", " ");        //依據工程部要求替換分隔符號
                        if (string.IsNullOrEmpty(warningArea)) {
                            throw new Exception("無法取得 CAP 元素內容 (EventWarningArea)");
                        }

                        result = string.Format(PUSH_TYPE_TEMPLATE[pushTypeKey], originTime, locationName, warningCriterion, warningArea);
                        break;
                }

            } catch (Exception) {
                throw;
            }

            return result;
        }

        /// <summary>
        /// 處理 TCP Socket 發送與接收處理
        /// </summary>
        /// <param name="pushContent">要發送的內容</param>
        /// <param name="useVersion">要使用的版本編號</param>
        private static void processingTcpSocket(string pushContent, string useVersion) {
            NetworkStream netWorkStream = null;
            StreamWriter streamWriter = null;

            try {
                using (TcpClient tcpClient = new TcpClient(getAppSettings("SOCKET_SERVER_HOST"),
                                                           Convert.ToInt16(getAppSettings("SOCKET_SERVER_PORT")))) {

                    if (tcpClient.Connected) {

                        using (netWorkStream = new NetworkStream(tcpClient.Client)) {
                            using (streamWriter = new StreamWriter(netWorkStream)) {

                                //產生要發送的 json 格式字串
                                string pushJsonString = string.Format("{0}{1}", new JavaScriptSerializer().Serialize(new {
                                    sequence = sequence,        //程序唯一識別碼
                                    push_type = paramsList[0].ToLowerInvariant(),       //要發送的資料類型
                                    identifier = paramsList[1],       //cap 唯一識別碼
                                    is_test = Convert.ToInt16(isExecTest),      //是否為測試模式
                                    push_content = pushContent,      //要產生文字檔的內容
                                    use_version = useVersion        //要使用的版本編號
                                }), PUSH_END_POINT);

                                //若伺服器回傳處理失敗則重新發送資料
                                Int16 pushRepeatCount = Convert.ToInt16(getAppSettings("PUSH_REPEAT_COUNT"));
                                for (int i = 0; i < pushRepeatCount; i++) {

                                    //發送資料至 server
                                    if (netWorkStream.CanWrite) {
                                        streamWriter.Write(pushJsonString);
                                        streamWriter.Flush();

                                        if (i == 0) {
                                            writeEventLog(EVENT_TYPE.PUSHED_TO_SERVER, "速報資料已發送至伺服器並等待回應中", false);
                                        }
                                    }

                                    //接收 server 回應結果，回應成功則跳出迴圈
                                    if (netWorkStream.CanRead) {
                                        byte[] data = new byte[8];
                                        int receiveLength = netWorkStream.Read(data, 0, data.Length);
                                        string responseCode = Encoding.UTF8.GetString(data, 0, receiveLength);

                                        //responseCode：
                                        //  1： 建立速報資料文字檔與寫入歷史記錄成功
                                        //  2： 建立速報資料文字檔成功但寫入歷史記錄失敗
                                        //  -1： 建立速報資料文字檔失敗
                                        //  -2： 建立速報資料文字檔失敗(傳入的速報資料在歷史紀錄內發現重複)
                                        //  -3： 建立速報資料文字檔失敗(讀取歷史紀錄資料發生錯誤)

                                        if (responseCode.Equals("1") || responseCode.Equals("2")) {
                                            writeEventLog(EVENT_TYPE.PROC_SUCCESS_FINISH, string.Format("伺服器回應速報資料處理成功 ({0})", responseCode), false);
                                            break;
                                        } else {

                                            //已執行完最後一次發送或者伺服器回應傳入的速報資料在歷史紀錄內發現重複及跳出重新發送迴圈
                                            if (responseCode.Equals("-2") || i == (pushRepeatCount - 1)) {
                                                writeEventLog(EVENT_TYPE.SERVER_HANDLE_ERROR, string.Format("伺服器回應速報資料處理失敗 ({0})", responseCode), false);
                                                break;
                                            }
                                        }
                                    }

                                    //若伺服器回傳處理失敗則延遲後再發送
                                    Thread.Sleep(Convert.ToInt16(getAppSettings("ERROR_DELAY_TIME")));
                                }
                            }
                        }
                    }
                }
            } catch (Exception) {
                if (netWorkStream != null) {
                    netWorkStream.Close();
                    netWorkStream.Dispose();
                }

                if (streamWriter != null) {
                    streamWriter.Close();
                    streamWriter.Dispose();
                }

                throw;
            }
        }

        /// <summary> 
        /// 產生不重複的亂數 (「產生亂數的範圍上限」至「「產生亂數的範圍下限」」之間的個數，不可小於「產生亂數的數量」，否則會跑不出迴圈)
        /// </summary> 
        /// <param name="intLower">產生亂數的範圍下限 </param>
        /// <param name="intUpper">產生亂數的範圍上限 </param>
        /// <param name="intNum">產生亂數的數量 </param>
        /// <returns></returns> 
        private static List<int> makeRand(int intLower, int intUpper, int intNum) {
            List<int> arrayRand = new List<int>();

            Random random = new Random((int)DateTime.Now.Ticks);
            int intRnd;
            while (arrayRand.Count < intNum) {
                intRnd = random.Next(intLower, intUpper + 1);
                if (!arrayRand.Contains(intRnd)) {
                    arrayRand.Add(intRnd);
                }
            }

            return arrayRand;
        }

        /// <summary>
        /// 取得 web.config 或 app.config AppSettings 值
        /// </summary>
        /// <param name="key">AppSettings key 名</param>
        /// <returns></returns>
        private static string getAppSettings(string key) {
            return ConfigurationManager.AppSettings[key];
        }

        /// <summary>
        /// 建立或加入文字檔案內容
        /// </summary>
        /// <param name="dir">文字檔案存放路徑</param>
        /// <param name="fileNameWithExt">檔名 + 副檔名</param>
        /// <param name="contentList">文字檔案內容集合</param>
        /// <returns>List<object></returns>
        private static List<object> createOrAppendFile(string dir, string fileNameWithExt, List<string> contentList) {
            List<object> resultList = new List<object>();

            if (!Directory.Exists(dir)) {
                Directory.CreateDirectory(dir);
            }

            try {
                File.AppendAllLines(string.Concat(dir, fileNameWithExt), contentList, Encoding.UTF8);
                resultList.Add(1);
            } catch (Exception ex) {
                resultList.Add(0);
                resultList.Add(ex.Message);
            }

            return resultList;
        }

        /// <summary>
        /// 將字串集合內的文字利用指定符號連結成字串組
        /// </summary>
        /// <param name="list">字串集合</param>
        /// <param name="symbo">指定符號</param>
        /// <returns></returns>
        private static string concatListString(List<string> list, string symbo) {
            string msgTypes = string.Empty;
            list.ForEach(str => msgTypes += ((msgTypes.Length > 0) ? symbo : string.Empty) + str);
            return msgTypes;
        }


    }
}
