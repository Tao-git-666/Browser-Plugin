namespace CrmLogicLens.Api.Errors;

public sealed class ApiInputException : Exception
{
    public ApiInputException(string detail, string? field = null, int statusCode = StatusCodes.Status400BadRequest)
        : base(detail)
    {
        Field = field;
        StatusCode = statusCode;
    }

    public string? Field { get; }

    public int StatusCode { get; }
}
