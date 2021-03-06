﻿using DirectSp.Exceptions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Web;

namespace DirectSp
{
    public class DirectSpHttpHandler
    {
        private readonly string _basePath;
        private readonly DirectSpInvoker _invoker;
        private string DownloadRecordsetPath => $"/{_basePath}/download/recordset";

        public DirectSpHttpHandler(DirectSpInvoker invoker, string basePath)
        {
            _invoker = invoker;
            _basePath = basePath;
        }

        private bool IsUriMatch(string path)
        {
            return path.IndexOf("/" + _basePath + "/") == 0 || path == "/" + _basePath;
        }

        public async Task<HttpResponseMessage>  Process(HttpRequestMessage requestMessage)
        {
            var uri = requestMessage.RequestUri;
            var path = uri.AbsolutePath.TrimEnd('/');
            var lastSegment = Path.GetFileName(path);

            //match URI
            if (!IsUriMatch(path))
                return null;

            // prepare json serialize
            var jsonSerializerSettings = new JsonSerializerSettings()
            {
                Converters = new JsonConverter[] { new StringEnumConverter() }
            };

            if (_invoker.UseCamelCase)
                jsonSerializerSettings.ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver();

            if (path.Equals(DownloadRecordsetPath, StringComparison.InvariantCultureIgnoreCase))
                return DownloadRecorset(requestMessage);

            // parse request

            var json = await requestMessage.Content.ReadAsStringAsync();
            var requestRemoteIp = requestMessage.Properties.ContainsKey("RemoteEndPoint") ? ((IPEndPoint)requestMessage.Properties["RemoteEndPoint"]).Address : null;
            var isLocalIp = requestRemoteIp == null || IPAddress.IsLoopback(requestRemoteIp);

            var spInvokeParams = new InvokeOptions
            {
                AuthUserId = (string)requestMessage.Properties["AuthUserId"],
                RequestRemoteIp = requestRemoteIp,
                IsLocalRequest = requestMessage.Properties.ContainsKey("MS_IsLocal") ? (bool)requestMessage.Properties["MS_IsLocal"] : isLocalIp,
                ApiInvokeOptions = null,
                UserAgent = requestMessage.Headers.UserAgent?.ToString(),
                RecordsetDownloadUrlTemplate = new UriBuilder(uri) { Path = DownloadRecordsetPath, Query = "id={id}&filename={filename}" }.ToString(),
            };

            // process
            var response = new HttpResponseMessage();
            try
            {
                object result = null;
                if (lastSegment == "invokebatch")
                {
                    var invokeParamsBatch = JsonConvert.DeserializeObject<ApiInvokeParamsBatch>(json);
                    spInvokeParams.ApiInvokeOptions = invokeParamsBatch.InvokeOptions;
                    result = await _invoker.Invoke(invokeParamsBatch.SpCalls, spInvokeParams);
                }
                else
                {
                    var invokeParams = JsonConvert.DeserializeObject<ApiInvokeParams>(json);
                    spInvokeParams.ApiInvokeOptions = invokeParams.InvokeOptions;
                    result = await _invoker.Invoke(invokeParams.SpCall, spInvokeParams);
                }

                response.Content = new StringContent(JsonConvert.SerializeObject(result, jsonSerializerSettings), System.Text.Encoding.UTF8, "application/json");
                response.StatusCode = HttpStatusCode.OK;
            }
            catch (Exception ex)
            {
                var dspError = ex is DirectSpException exception ? exception : new DirectSpException(ex);
                response.Content = new StringContent(JsonConvert.SerializeObject(dspError.SpCallError, jsonSerializerSettings));
                response.StatusCode = dspError.StatusCode;
            }

            //add headers
            response.Headers.Add("DSP-AppVersion", _invoker.AppVersion);
            response.Headers.Add("Access-Control-Expose-Headers", "DSP-AppVersion");
            return response;
        }

        private HttpResponseMessage DownloadRecorset(HttpRequestMessage requestMessage)
        {
            try
            {
                var queryParams = HttpUtility.ParseQueryString(requestMessage.RequestUri.Query);
                var id = queryParams.Get("id");
                var fileName = queryParams.Get("filename");
                if (id == null || fileName == null)
                    throw new FileNotFoundException();

                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = "result.csv";

                // Check file existance
                var filePath = Path.Combine(_invoker.InvokerPath.RecordsetsFolder, id);
                if (!File.Exists(filePath))
                    throw new FileNotFoundException();

                // Create content
                var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var response = new HttpResponseMessage
                {
                    Content = new StreamContent(fs),
                    StatusCode = HttpStatusCode.OK
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");

                //Add headers
                response.Headers.Add("DSP-AppVersion", _invoker.AppVersion);
                response.Headers.Add("Access-Control-Expose-Headers", "DSP-AppVersion");
                response.Content.Headers.Add("content-disposition", $"attachment; filename=\"{fileName}\"");
                return response;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }
    }
}
