using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;
using ServiceStack.Configuration;
using ServiceStack.Logging;
using ServiceStack.Messaging;
using ServiceStack.Serialization;
using ServiceStack.Server;
using ServiceStack.Text;
using ServiceStack.WebHost.Endpoints;

namespace ServiceStack.ServiceHost
{
    public delegate object ServiceExecFn(IRequestContext requestContext, object request);
    public delegate object InstanceExecFn(IRequestContext requestContext, object intance, object request);
    public delegate object ActionInvokerFn(object intance, object request);
    public delegate void VoidActionInvokerFn(object intance, object request);

    public class ServiceController
        : IServiceController
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(ServiceController));
        private const string ResponseDtoSuffix = "Response";

        public ServiceController(Func<IEnumerable<Type>> resolveServicesFn, ServiceMetadata metadata = null)
        {
            this.Metadata = metadata ?? new ServiceMetadata();

            this.RequestTypeFactoryMap = new Dictionary<Type, Func<IHttpRequest, object>>();
            this.EnableAccessRestrictions = true;
            this.ResolveServicesFn = resolveServicesFn;
        }

        readonly Dictionary<Type, ServiceExecFn> requestExecMap
			= new Dictionary<Type, ServiceExecFn>();

        readonly Dictionary<Type, RestrictAttribute> requestServiceAttrs
			= new Dictionary<Type, RestrictAttribute>();

        public bool EnableAccessRestrictions { get; set; }

        public ServiceMetadata Metadata { get; internal set; }

        public Dictionary<Type, Func<IHttpRequest, object>> RequestTypeFactoryMap { get; set; }

        public string DefaultOperationsNamespace { get; set; }

        public IServiceRoutes Routes { get { return Metadata.Routes; } }

        private IResolver resolver;
        public IResolver Resolver
        {
            get { return resolver ?? EndpointHost.AppHost; }
            set { resolver = value; }
        }

        public Func<IEnumerable<Type>> ResolveServicesFn { get; set; }

        public void Register(ITypeFactory serviceFactoryFn)
        {
            foreach (var serviceType in ResolveServicesFn())
            {
                RegisterService(serviceFactoryFn, serviceType);
            }
        }

        public void RegisterService(ITypeFactory serviceFactoryFn, Type serviceType)
        {
            var processedReqs = new HashSet<Type>();

            if (typeof(IService).IsAssignableFrom(serviceType)
                && !serviceType.IsAbstract && !serviceType.IsGenericTypeDefinition && !serviceType.ContainsGenericParameters)
            {
                foreach (var mi in serviceType.GetActions())
                {
                    var requestType = mi.GetParameters()[0].ParameterType;
                    if (processedReqs.Contains(requestType)) continue;
                    processedReqs.Add(requestType);

                    RegisterNServiceExecutor(requestType, serviceType, serviceFactoryFn);

                    var returnMarker = requestType.GetTypeWithGenericTypeDefinitionOf(typeof(IReturn<>));
                    var responseType = returnMarker != null ?
                          returnMarker.GetGenericArguments()[0]
                        : mi.ReturnType != typeof(object) && mi.ReturnType != typeof(void) ?
                          mi.ReturnType
                        : AssemblyUtils.FindType(requestType.FullName + ResponseDtoSuffix);

                    RegisterRestPaths(requestType);

                    Metadata.Add(serviceType, requestType, responseType);

                    if (typeof(IRequiresRequestStream).IsAssignableFrom(requestType))
                    {
                        this.RequestTypeFactoryMap[requestType] = httpReq =>
                        {
                            var rawReq = (IRequiresRequestStream)requestType.CreateInstance();
                            rawReq.RequestStream = httpReq.InputStream;
                            return rawReq;
                        };
                    }

                    Log.DebugFormat("Registering {0} service '{1}' with request '{2}'",
                        (responseType != null ? "Reply" : "OneWay"), serviceType.Name, requestType.Name);
                }
            }
        }

        public readonly Dictionary<string, List<RestPath>> RestPathMap = new Dictionary<string, List<RestPath>>();

        public void RegisterRestPaths(Type requestType)
        {
            var attrs = TypeDescriptor.GetAttributes(requestType).OfType<RouteAttribute>();
            foreach (RouteAttribute attr in attrs)
            {
                var restPath = new RestPath(requestType, attr.Path, attr.Verbs, attr.Summary, attr.Notes);

                var defaultAttr = attr as FallbackRouteAttribute;
                if (defaultAttr != null)
                {
                    if (EndpointHost.Config != null)
                    {
                        if (EndpointHost.Config.FallbackRestPath != null)
                            throw new NotSupportedException(string.Format(
                                "Config.FallbackRestPath is already defined. Only 1 [FallbackRoute] is allowed."));

                        EndpointHost.Config.FallbackRestPath = (httpMethod, pathInfo, filePath) =>
                        {
                            var pathInfoParts = RestPath.GetPathPartsForMatching(pathInfo);
                            return restPath.IsMatch(httpMethod, pathInfoParts) ? restPath : null;
                        };
                    }
                    
                    continue;
                }

                if (!restPath.IsValid)
                    throw new NotSupportedException(string.Format(
                        "RestPath '{0}' on Type '{1}' is not Valid", attr.Path, requestType.Name));

                RegisterRestPath(restPath);
            }
        }

        private static readonly char[] InvalidRouteChars = new[] {'?', '&'};

        public void RegisterRestPath(RestPath restPath)
        {
            if (!EndpointHostConfig.SkipRouteValidation)
            {
                if (!restPath.Path.StartsWith("/"))
                    throw new ArgumentException("Route '{0}' on '{1}' must start with a '/'".Fmt(restPath.Path, restPath.RequestType.Name));
                if (restPath.Path.IndexOfAny(InvalidRouteChars) != -1)
                    throw new ArgumentException(("Route '{0}' on '{1}' contains invalid chars. " +
                                                "See https://github.com/ServiceStack/ServiceStack/wiki/Routing for info on valid routes.").Fmt(restPath.Path, restPath.RequestType.Name));
            }

            List<RestPath> pathsAtFirstMatch;
            if (!RestPathMap.TryGetValue(restPath.FirstMatchHashKey, out pathsAtFirstMatch))
            {
                pathsAtFirstMatch = new List<RestPath>();
                RestPathMap[restPath.FirstMatchHashKey] = pathsAtFirstMatch;
            }
            pathsAtFirstMatch.Add(restPath);
        }

        public void AfterInit()
        {
            //Register any routes configured on Metadata.Routes
            foreach (var restPath in this.Metadata.Routes.RestPaths)
            {
                RegisterRestPath(restPath);
            }

            //Sync the RestPaths collections
            Metadata.Routes.RestPaths.Clear();
            Metadata.Routes.RestPaths.AddRange(RestPathMap.Values.SelectMany(x => x));

            Metadata.AfterInit();
        }

        public IRestPath GetRestPathForRequest(string httpMethod, string pathInfo)
        {
            var matchUsingPathParts = RestPath.GetPathPartsForMatching(pathInfo);

            List<RestPath> firstMatches;

            var yieldedHashMatches = RestPath.GetFirstMatchHashKeys(matchUsingPathParts);
            foreach (var potentialHashMatch in yieldedHashMatches)
            {
                if (!this.RestPathMap.TryGetValue(potentialHashMatch, out firstMatches)) continue;

                var bestScore = -1;
                foreach (var restPath in firstMatches)
                {
                    var score = restPath.MatchScore(httpMethod, matchUsingPathParts);
                    if (score > bestScore) bestScore = score;
                }
                if (bestScore > 0)
                {
                    foreach (var restPath in firstMatches)
                    {
                        if (bestScore == restPath.MatchScore(httpMethod, matchUsingPathParts))
                            return restPath;
                    }
                }
            }

            var yieldedWildcardMatches = RestPath.GetFirstMatchWildCardHashKeys(matchUsingPathParts);
            foreach (var potentialHashMatch in yieldedWildcardMatches)
            {
                if (!this.RestPathMap.TryGetValue(potentialHashMatch, out firstMatches)) continue;

                var bestScore = -1;
                foreach (var restPath in firstMatches)
                {
                    var score = restPath.MatchScore(httpMethod, matchUsingPathParts);
                    if (score > bestScore) bestScore = score;
                }
                if (bestScore > 0)
                {
                    foreach (var restPath in firstMatches)
                    {
                        if (bestScore == restPath.MatchScore(httpMethod, matchUsingPathParts))
                            return restPath;
                    }
                }
            }

            return null;
        }

        internal class TypeFactoryWrapper : ITypeFactory
        {
            private readonly Func<Type, object> typeCreator;

            public TypeFactoryWrapper(Func<Type, object> typeCreator)
            {
                this.typeCreator = typeCreator;
            }

            public object CreateInstance(Type type)
            {
                return typeCreator(type);
            }
        }

        public void RegisterNServiceExecutor(Type requestType, Type serviceType, ITypeFactory serviceFactoryFn)
        {
            var serviceExecDef = typeof(ServiceRequestExec<,>).MakeGenericType(serviceType, requestType);
            var iserviceExec = (IServiceExec)serviceExecDef.CreateInstance();

            ServiceExecFn handlerFn = (requestContext, dto) => {
                var service = serviceFactoryFn.CreateInstance(serviceType);

                ServiceExecFn serviceExec = (reqCtx, req) =>
                    iserviceExec.Execute(reqCtx, service, req);

                return ManagedServiceExec(serviceExec, service, requestContext, dto);
            };

            AddToRequestExecMap(requestType, serviceType, handlerFn);
        }

        private void AddToRequestExecMap(Type requestType, Type serviceType, ServiceExecFn handlerFn)
        {
            if (requestExecMap.ContainsKey(requestType))
            {
                throw new AmbiguousMatchException(
                    string.Format(
                    "Could not register Request '{0}' with service '{1}' as it has already been assigned to another service.\n"
                    + "Each Request DTO can only be handled by 1 service.",
                    requestType.FullName, serviceType.FullName));
            }

            requestExecMap.Add(requestType, handlerFn);

            var serviceAttrs = requestType.GetCustomAttributes(typeof(RestrictAttribute), false);
            if (serviceAttrs.Length > 0)
            {
                requestServiceAttrs.Add(requestType, (RestrictAttribute)serviceAttrs[0]);
            }
        }

        private static object ManagedServiceExec(
            ServiceExecFn serviceExec,
            object service, IRequestContext requestContext, object dto)
        {
            try
            {
                InjectRequestContext(service, requestContext);

                try
                {
                    if (EndpointHost.Config != null && EndpointHost.Config.PreExecuteServiceFilter != null)
                    {
                        EndpointHost.Config.PreExecuteServiceFilter(service, 
                            requestContext.Get<IHttpRequest>(),
                            requestContext.Get<IHttpResponse>());
                    }

                    //Executes the service and returns the result
                    var response = serviceExec(requestContext, dto);

                    if (EndpointHost.Config != null && EndpointHost.Config.PostExecuteServiceFilter != null)
                    {
                        EndpointHost.Config.PostExecuteServiceFilter(service, 
                            requestContext.Get<IHttpRequest>(),
                            requestContext.Get<IHttpResponse>());

                    }

                    return response;
                }
                finally
                {
                    if (EndpointHost.AppHost != null)
                    {
                        //Gets disposed by AppHost or ContainerAdapter if set
                        EndpointHost.AppHost.Release(service);
                    }
                    else
                    {
                        using (service as IDisposable) { }
                    }
                }
            }
            catch (TargetInvocationException tex)
            {
                //Mono invokes using reflection
                throw tex.InnerException ?? tex;
            }
        }

        private static void InjectRequestContext(object service, IRequestContext requestContext)
        {
            if (requestContext == null) return;

            var serviceRequiresContext = service as IRequiresRequestContext;
            if (serviceRequiresContext != null)
            {
                serviceRequiresContext.RequestContext = requestContext;
            }

            var servicesRequiresHttpRequest = service as IRequiresHttpRequest;
            if (servicesRequiresHttpRequest != null)
                servicesRequiresHttpRequest.HttpRequest = requestContext.Get<IHttpRequest>();
        }
        
        //Execute MQ
        public object ExecuteMessage<T>(IMessage<T> mqMessage)
        {
            return Execute(mqMessage.Body, new MqRequestContext(this.Resolver, mqMessage));
        }

        //Execute MQ with requestContext
        public object ExecuteMessage<T>(IMessage<T> dto, IRequestContext requestContext)
        {
            return Execute(dto.Body, requestContext);
        }

        public object Execute(object request)
        {
            return Execute(request, null);
        }

        //Execute HTTP
        public object Execute(object request, IRequestContext requestContext)
        {
            var requestType = request.GetType();

            if (EnableAccessRestrictions)
            {
                AssertServiceRestrictions(requestType,
                    requestContext != null ? requestContext.EndpointAttributes : EndpointAttributes.None);
            }

            var handlerFn = GetService(requestType);
            return handlerFn(requestContext, request);
        }

        public ServiceExecFn GetService(Type requestType)
        {
            ServiceExecFn handlerFn;
            if (!requestExecMap.TryGetValue(requestType, out handlerFn))
            {
                throw new NotImplementedException(string.Format("Unable to resolve service '{0}'", requestType.Name));
            }

            return handlerFn;
        }

        public object ExecuteText(string requestXml, Type requestType, IRequestContext requestContext)
        {
            var request = DataContractDeserializer.Instance.Parse(requestXml, requestType);
            var response = Execute(request, requestContext);
            var responseXml = DataContractSerializer.Instance.Parse(response);
            return responseXml;
        }

        public void AssertServiceRestrictions(Type requestType, EndpointAttributes actualAttributes)
        {
            if (EndpointHost.Config != null && !EndpointHost.Config.EnableAccessRestrictions) return;

            RestrictAttribute restrictAttr;
            var hasNoAccessRestrictions = !requestServiceAttrs.TryGetValue(requestType, out restrictAttr)
                || restrictAttr.HasNoAccessRestrictions;

            if (hasNoAccessRestrictions)
            {
                return;
            }

            var failedScenarios = new StringBuilder();
            foreach (var requiredScenario in restrictAttr.AccessibleToAny)
            {
                var allServiceRestrictionsMet = (requiredScenario & actualAttributes) == actualAttributes;
                if (allServiceRestrictionsMet)
                {
                    return;
                }

                var passed = requiredScenario & actualAttributes;
                var failed = requiredScenario & ~(passed);

                failedScenarios.AppendFormat("\n -[{0}]", failed);
            }

            var internalDebugMsg = (EndpointAttributes.InternalNetworkAccess & actualAttributes) != 0
                ? "\n Unauthorized call was made from: " + actualAttributes
                : "";

            throw new UnauthorizedAccessException(
                string.Format("Could not execute service '{0}', The following restrictions were not met: '{1}'" + internalDebugMsg,
                    requestType.Name, failedScenarios));
        }
    }

}