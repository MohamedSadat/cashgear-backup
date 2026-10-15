using CashGear.Backup.Core;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace CashGear.Backup.Agent.Api;

/// <summary>
/// Turns agent and engine errors into gRPC statuses with the error kind in the "cashgear-error-kind"
/// trailer. Unexpected exceptions are logged and reported without their details.
/// </summary>
internal sealed partial class ErrorInterceptor(ILogger<ErrorInterceptor> logger) : Interceptor
{
    public const string ErrorKindTrailer = "cashgear-error-kind";

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (Exception ex) when (ex is not RpcException && !IsCallCanceled(ex, context))
        {
            throw Translate(ex, context);
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(request, responseStream, context);
        }
        catch (Exception ex) when (ex is not RpcException && !IsCallCanceled(ex, context))
        {
            throw Translate(ex, context);
        }
    }

    private static bool IsCallCanceled(Exception ex, ServerCallContext context) =>
        ex is OperationCanceledException && context.CancellationToken.IsCancellationRequested;

    private RpcException Translate(Exception ex, ServerCallContext context)
    {
        var (code, kind, message) = ex switch
        {
            AgentException agent => (agent.Error switch
            {
                AgentError.InvalidArgument => StatusCode.InvalidArgument,
                AgentError.NotFound => StatusCode.NotFound,
                AgentError.AlreadyExists => StatusCode.AlreadyExists,
                AgentError.PermissionDenied => StatusCode.PermissionDenied,
                AgentError.LicenceRequired => StatusCode.FailedPrecondition,
                _ => StatusCode.FailedPrecondition,
            }, agent.Error.ToString(), agent.Message),
            BackupEngineException engine => (engine.Kind switch
            {
                BackupErrorKind.InvalidRequest or BackupErrorKind.InvalidPassword => StatusCode.InvalidArgument,
                BackupErrorKind.RepositoryNotFound => StatusCode.NotFound,
                BackupErrorKind.RepositoryAlreadyExists => StatusCode.AlreadyExists,
                BackupErrorKind.DestinationUnreachable or BackupErrorKind.EngineUnavailable => StatusCode.Unavailable,
                BackupErrorKind.EngineBinaryRejected or BackupErrorKind.EngineProtocolError => StatusCode.Internal,
                _ => StatusCode.FailedPrecondition,
            }, engine.Kind.ToString(), engine.Message),
            _ => (StatusCode.Internal, "Internal", "The agent hit an unexpected error; details are in its log."),
        };

        if (code == StatusCode.Internal)
        {
            LogUnexpected(logger, ex, context.Method);
        }

        return new RpcException(new Status(code, message), new Metadata { { ErrorKindTrailer, kind } });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Call {Method} failed")]
    private static partial void LogUnexpected(ILogger logger, Exception exception, string method);
}
