using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Http;

namespace Immanuel.KeyValue
{
    public static class WebApiConfig
    {
        public static void Register(HttpConfiguration config)
        {
            // Web API configuration and services

            // Web API routes
            config.MapHttpAttributeRoutes();

            config.Routes.MapHttpRoute(
                name: "DefaultApi",
                routeTemplate: "api/{controller}/{action}/{appkey}/{key}/{value}",
                defaults: new { appkey = RouteParameter.Optional, key = RouteParameter.Optional, value = RouteParameter.Optional }
            );
        }
    }
}
