// https://github.com/dotnetcore/FastGithub/blob/2.1.4/FastGithub.HttpServer/HttpReverseProxyMiddleware.cs

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Logging;
using System.Application.Models;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Net;
using System.Text;
using Yarp.ReverseProxy.Forwarder;
using System;

namespace System.Application.Services.Implementation.HttpServer.Middleware;

/// <summary>
/// 反向代理中间件
/// </summary>
sealed class HttpReverseProxyMiddleware
{
    static readonly IDomainConfig defaultDomainConfig = new DomainConfig() { TlsSni = true, };

    readonly IHttpForwarder httpForwarder;
    readonly IReverseProxyHttpClientFactory httpClientFactory;
    readonly IReverseProxyConfig reverseProxyConfig;
    readonly ILogger<HttpReverseProxyMiddleware> logger;

    public HttpReverseProxyMiddleware(
        IHttpForwarder httpForwarder,
        IReverseProxyHttpClientFactory httpClientFactory,
        IReverseProxyConfig reverseProxyConfig,
        ILogger<HttpReverseProxyMiddleware> logger)
    {
        this.httpForwarder = httpForwarder;
        this.httpClientFactory = httpClientFactory;
        this.reverseProxyConfig = reverseProxyConfig;
        this.logger = logger;
    }

    /// <summary>
    /// 处理请求
    /// </summary>
    /// <param name="context"></param>
    /// <param name="next"></param>
    /// <returns></returns>
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var url = context.Request.GetDisplayUrl();

        var isScriptInject = reverseProxyConfig.TryGetScriptConfig(url, out var scriptConfigs);

        var originalBody = context.Response.Body;
        MemoryStream? memoryStream = null;

        if (isScriptInject)
        {
            memoryStream = new MemoryStream();
            context.Response.Body = memoryStream;
        }

        if (TryGetDomainConfig(url, out var domainConfig) == false ||
            IReverseProxyService.Instance.OnlyEnableProxyScript)
        {
            await next(context);
            if (isScriptInject)
                await HandleScriptInject(context, scriptConfigs, memoryStream!, originalBody!);
            return;
        }

        if (domainConfig.Items.Any_Nullable())
            domainConfig = RecursionMatchDomainConfig(url, domainConfig);

        if (domainConfig.Response == null)
        {
            if (IReverseProxyService.Instance.EnableHttpProxyToHttps && context.Request.Scheme == Uri.UriSchemeHttp)
            {
                context.Response.Redirect(Uri.UriSchemeHttps + context.Request.Host.Host + context.Request.RawUrl());
                return;
            }

            var destinationPrefix = GetDestinationPrefix(context.Request.Scheme, context.Request.Host, domainConfig.Destination);
            var httpClient = httpClientFactory.CreateHttpClient(context.Request.Host.Host, domainConfig);
            if (!string.IsNullOrEmpty(domainConfig.UserAgent))
            {
                context.Request.Headers.UserAgent = domainConfig.UserAgent.Replace("${origin}", context.Request.Headers.UserAgent, StringComparison.OrdinalIgnoreCase);
            }

            var error = await httpForwarder.SendAsync(context, destinationPrefix, httpClient, ForwarderRequestConfig.Empty, HttpTransformer.Empty);

            if (error != ForwarderError.None)
            {
                await HandleErrorAsync(context, error);
            }
            else if (isScriptInject)
            {
                await HandleScriptInject(context, scriptConfigs, memoryStream!, originalBody!);
            }
        }
        else
        {
            context.Response.StatusCode = (int)domainConfig.Response.StatusCode;
            context.Response.ContentType = domainConfig.Response.ContentType;
            if (domainConfig.Response.ContentValue != null)
            {
                await context.Response.WriteAsync(domainConfig.Response.ContentValue);
            }
        }
    }

    /// <summary>
    /// 递归匹配子域名配置
    /// </summary>
    /// <param name="url"></param>
    /// <param name="domainConfig"></param>
    /// <returns></returns>
    static IDomainConfig RecursionMatchDomainConfig(string url, IDomainConfig domainConfig)
    {
        if (domainConfig.Items.Any_Nullable())
        {
            var item = domainConfig.Items.FirstOrDefault(s => s.Key.IsMatch(url)).Value;
            if (item != null)
                return RecursionMatchDomainConfig(url, item);
        }
        return domainConfig;
    }

    bool TryGetDomainConfig(string uri, [MaybeNullWhen(false)] out IDomainConfig domainConfig)
    {
        if (reverseProxyConfig.TryGetDomainConfig(uri, out domainConfig) == true)
        {
            return true;
        }

        var host = new Uri(uri).Host;
        // 未配置的域名，但仍然被解析到本机 IP 的域名
        if (OperatingSystem.IsWindows() && IsDomain(host))
        {
            logger.LogWarning(
                $"域名 {host} 可能已经被 DNS 污染，如果域名为本机域名，请解析为非回环 IP。");
            domainConfig = defaultDomainConfig;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 是否为域名
    /// </summary>
    /// <param name="host"></param>
    /// <returns></returns>
    static bool IsDomain(string host) => !IPAddress.TryParse(host, out _) && host.Contains('.');

    /// <summary>
    /// 获取目标前缀
    /// </summary>
    /// <param name="scheme"></param>
    /// <param name="host"></param>
    /// <param name="destination"></param>
    /// <returns></returns>
    string GetDestinationPrefix(string scheme, HostString host, Uri? destination)
    {
        var defaultValue = $"{scheme}://{host}/";
        if (destination == null)
        {
            return defaultValue;
        }

        var baseUri = new Uri(defaultValue);
        var result = new Uri(baseUri, destination).ToString();
        logger.LogInformation($"{defaultValue} => {result}");
        return result;
    }

    /// <summary>
    /// 处理错误信息
    /// </summary>
    /// <param name="context"></param>
    /// <param name="error"></param>
    /// <returns></returns>
    static async Task HandleErrorAsync(HttpContext context, ForwarderError error)
    {
        await context.Response.WriteAsJsonAsync(new
        {
            error = error.ToString(),
            message = context.GetForwarderErrorFeature()?.Exception?.Message,
        });
    }

    /// <summary>
    /// 处理脚本注入内容
    /// </summary>
    /// <param name="context"></param>
    /// <param name="scripts"></param>
    static async Task HandleScriptInject(HttpContext context, IEnumerable<IScriptConfig>? scripts, MemoryStream memoryStream, Stream originalBody)
    {
        async Task ResetBody()
        {
            memoryStream.Seek(0, SeekOrigin.Begin);
            context.Response.ContentLength = memoryStream.Length;
            await memoryStream.CopyToAsync(originalBody);
            context.Response.Body = originalBody;
        }

        if (!scripts.Any_Nullable() ||
            context.Request.Method != HttpMethods.Get ||
            context.Response.StatusCode != StatusCodes.Status200OK ||
            !context.Response.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            await ResetBody();
            return;
        }

        if (IReverseProxyService.Instance.IsOnlyWorkSteamBrowser && context.Request.UserAgent()?.Contains("Valve Steam") == false)
        {
            await ResetBody();
            return;
        }

        if (!string.IsNullOrEmpty(context.Response.Headers.ContentSecurityPolicy))
        {
            //var csp = context.Response.Headers.ContentSecurityPolicy.ToString();
            //var marks = new string[] { "default-src", "script-src", "connect-src" };

            //foreach (var mark in marks)
            //{
            //    var cspIndex = csp.IndexOf(mark);
            //    if (cspIndex >= 0)
            //    {
            //        context.Response.Headers.ContentSecurityPolicy = csp.Insert(cspIndex + mark.Length, " " + IReverseProxyService.LocalDomain);
            //    }
            //}
            context.Response.Headers.Remove("Content-Security-Policy");
        }

        StringBuilder scriptHtml = new();
        foreach (var script in scripts)
        {
            var temp = $"<script type=\"text/javascript\" src=\"https://{IReverseProxyService.LocalDomain}/{script.LocalId}\"></script>";
            scriptHtml.AppendLine(temp);
        }

        if (scriptHtml.Length > 0)
        {
            try
            {
                memoryStream.Seek(0, SeekOrigin.Begin);
                using Stream s = (string)context.Response.Headers.ContentEncoding switch
                {
                    "gzip" => new GZipStream(memoryStream, CompressionMode.Decompress, true),
                    "deflate" => new DeflateStream(memoryStream, CompressionMode.Decompress, true),
                    "br" => new BrotliStream(memoryStream, CompressionMode.Decompress, true),
                    _ => memoryStream,
                };
                var encoding = context.Response.GetEncodingFromContentType();
                var responseBody = await new StreamReader(s, encoding).ReadToEndAsync();
                memoryStream.Seek(0, SeekOrigin.Begin);

                var index = responseBody.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (index == -1)
                    index = responseBody.LastIndexOf("</head>", StringComparison.OrdinalIgnoreCase);
                if (index > -1)
                {
                    responseBody = responseBody.Insert(index, scriptHtml.ToString());
                    var buffer = encoding.GetBytes(responseBody);
                    using var compressionStream = new MemoryStream();
                    using Stream? zip = (string)context.Response.Headers.ContentEncoding switch
                    {
                        "gzip" => new GZipStream(compressionStream, CompressionMode.Compress, true),
                        "deflate" => new DeflateStream(compressionStream, CompressionMode.Compress, true),
                        "br" => new BrotliStream(compressionStream, CompressionMode.Compress, true),
                        _ => null,
                    };

                    if (zip != null)
                    {
                        await zip.WriteAsync(buffer);
                        await zip.DisposeAsync(); //不主动释放压缩流会有残余数据未写入compressionStream
                    }
                    else
                    {
                        await compressionStream.WriteAsync(buffer);
                        context.Response.Headers.Remove("Content-Encoding");
                    }

                    context.Response.ContentLength = compressionStream.Length;
                    compressionStream.Seek(0, SeekOrigin.Begin);
                    await compressionStream.CopyToAsync(originalBody);
                    context.Response.Body = originalBody;
                }
            }
            catch
            {
                await ResetBody();
            }
            finally
            {
                await memoryStream.DisposeAsync();
            }
        }
    }
}