using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.ServiceModel.Channels;
using System.Web;
using System.Web.Http;

namespace Immanuel.KeyValue.Controllers
{
    public class KeyValController : ApiController
    {
        static string connection_string = @"";
        static int cnt = 0;
        // GET api/<controller>
        public string GetAppKey()
        {
            return RandomString(8);
        }

        public int GetCount()
        {
            int str = 0;
            using (System.Data.SqlClient.SqlConnection con = new System.Data.SqlClient.SqlConnection(connection_string))
            {
                using (System.Data.SqlClient.SqlCommand cmd = new System.Data.SqlClient.SqlCommand())
                {
                    cmd.CommandText = "select count(*) from [KeyVal]";
                    cmd.CommandType = System.Data.CommandType.Text;
                    cmd.Connection = con;
                    con.Open();
                    var obj = cmd.ExecuteScalar();
                    str = (int)(obj ?? str);
                }
            }
            return str;
        }

        public string GetValue(string appkey, string key)
        {
            string str = "";
            using (System.Data.SqlClient.SqlConnection con = new System.Data.SqlClient.SqlConnection(connection_string))
            {
                using (System.Data.SqlClient.SqlCommand cmd = new System.Data.SqlClient.SqlCommand())
                {
                    cmd.CommandText = "[immanuel_sa].[sp_SelectKeyVal]";
                    cmd.CommandType = System.Data.CommandType.StoredProcedure;
                    cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@ip_ClientKey", appkey));
                    cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@ip_KeyName", key));
                    cmd.Connection = con;
                    con.Open();
                    var obj = cmd.ExecuteScalar();
                    str = (string)(obj ?? str);
                }
            }
            return str;
        }

        [HttpPost]
        public string ActOnValue(string appkey, string key, string value)
        {
            try
            {
                using (System.Data.SqlClient.SqlConnection con = new System.Data.SqlClient.SqlConnection(connection_string))
                {
                    using (System.Data.SqlClient.SqlCommand cmd = new System.Data.SqlClient.SqlCommand())
                    {
                        cmd.CommandText = "[immanuel_sa].[sp_UpdateAction]";
                        cmd.CommandType = System.Data.CommandType.StoredProcedure;
                        cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@ip_ClientKey", appkey));
                        cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@ip_KeyName", key));
                        cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@ip_Action", value));
                        cmd.Connection = con;
                        con.Open();
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch
            {
                return "Increment Failed, increment applied on string charecters";
            }
            return "Increment Successful";
        }

        [HttpPost]
        public bool UpdateValue(string appkey, string key, string value = null)
        {
            UpdateVal(appkey, key, value);
            return true;
        }

        void UpdateVal(string appkey, string key, string value)
        {
            using (System.Data.SqlClient.SqlConnection con = new System.Data.SqlClient.SqlConnection(connection_string))
            {
                using (System.Data.SqlClient.SqlCommand cmd = new System.Data.SqlClient.SqlCommand())
                {
                    cmd.CommandText = "[immanuel_sa].[sp_UpdateKeyVal]";
                    cmd.CommandType = System.Data.CommandType.StoredProcedure;
                    cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@ip_ClientKey", appkey));
                    cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@ip_KeyName", key));
                    cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@ip_KeyVal", value));
                    cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@ip_IpAddr", GetIp()));
                    cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@ip_Agent", Request.Headers.UserAgent.ToString()));
                    cmd.Connection = con;
                    con.Open();
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public string GetIp()
        {
            return GetClientIp();
        }

        private string GetClientIp(HttpRequestMessage request = null)
        {
            request = request ?? Request;

            if (request.Properties.ContainsKey("MS_HttpContext"))
            {
                return ((HttpContextWrapper)request.Properties["MS_HttpContext"]).Request.UserHostAddress;
            }
            else if (request.Properties.ContainsKey(RemoteEndpointMessageProperty.Name))
            {
                RemoteEndpointMessageProperty prop = (RemoteEndpointMessageProperty)request.Properties[RemoteEndpointMessageProperty.Name];
                return prop.Address;
            }
            else if (HttpContext.Current != null)
            {
                return HttpContext.Current.Request.UserHostAddress;
            }
            else
            {
                return null;
            }
        }

        protected string RandomString(int Size)
        {
            string input = "abcdefghijklmnopqrstuvwxyz0123456789";
            var chars = Enumerable.Range(0, Size)
                                   .Select(x => input[random.Next(0, input.Length)]);
            return new string(chars.ToArray());
        }
        static Random random = new Random();
    }
}