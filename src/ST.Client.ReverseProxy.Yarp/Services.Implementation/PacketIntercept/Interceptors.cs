#if WINDOWS

namespace System.Application.Services.Implementation.PacketIntercept;

/// <summary>
/// 多个拦截器
/// </summary>
sealed class Interceptors : IInterceptor
{
    readonly IEnumerable<IInterceptor> interceptors;

    public Interceptors(IEnumerable<IInterceptor> interceptors)
    {
        this.interceptors = interceptors;
    }

    Task IInterceptor.InterceptAsync(CancellationToken cancellationToken)
    {
        var tasks = interceptors.Select(item => item.InterceptAsync(cancellationToken));
        return Task.WhenAll(tasks);
    }
}

#endif