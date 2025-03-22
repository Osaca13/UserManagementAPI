using System.Collections.Concurrent;
using Microsoft.Extensions.Primitives;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        // Define an in-memory ConcurrentDictionary to store users
        var users = new ConcurrentDictionary<string, User>(StringComparer.OrdinalIgnoreCase);

        // Prepopulate the dictionary with some users
        users.TryAdd("Alice", new User("Alice", 25));
        users.TryAdd("Bob", new User("Bob", 30));
        users.TryAdd("Charlie", new User("Charlie", 35));

        // Configure middleware pipeline in the correct order
        app.UseMiddleware<ErrorHandlingMiddleware>(); // Error-handling middleware first
        app.UseMiddleware<AuthenticationMiddleware>(); // Authentication middleware next
        app.UseMiddleware<LoggingMiddleware>(); // Logging middleware last

        // Get all users
        app.MapGet("/users", () => users.Values);

        // Get a single user by username
        app.MapGet("/users/{username}", (string username) =>
        {
            return users.TryGetValue(username, out var user) ? Results.Ok(user) : Results.NotFound();
        });

        // Create a new user with validation
        app.MapPost("/users", (User newUser) =>
        {
            try
            {
                // Validate UserName
                if (string.IsNullOrWhiteSpace(newUser.UserName) || newUser.UserName.Length < 3)
                {
                    return Results.BadRequest("UserName must be at least 3 characters long and cannot be empty.");
                }

                if (newUser.UserName.Any(char.IsWhiteSpace))
                {
                    return Results.BadRequest("UserName cannot contain spaces.");
                }

                // Validate UserAge
                if (newUser.UserAge < 0 || newUser.UserAge > 120)
                {
                    return Results.BadRequest("UserAge must be between 0 and 120.");
                }

                // Check for duplicate username
                if (!users.TryAdd(newUser.UserName, newUser))
                {
                    return Results.BadRequest("A user with the same username already exists.");
                }

                return Results.Created($"/users/{newUser.UserName}", newUser);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return Results.Problem("An unexpected error occurred. Please try again later.");
            }
        });

        // Update a user by username with validation
        app.MapPut("/users/{username}", (string username, User updatedUser) =>
        {
            try
            {
                // Validate UserName
                if (string.IsNullOrWhiteSpace(updatedUser.UserName) || updatedUser.UserName.Length < 3)
                {
                    return Results.BadRequest("UserName must be at least 3 characters long and cannot be empty.");
                }

                if (updatedUser.UserName.Any(char.IsWhiteSpace))
                {
                    return Results.BadRequest("UserName cannot contain spaces.");
                }

                // Validate UserAge
                if (updatedUser.UserAge < 0 || updatedUser.UserAge > 120)
                {
                    return Results.BadRequest("UserAge must be between 0 and 120.");
                }

                // Check if the user exists
                if (!users.TryGetValue(username, out var existingUser))
                {
                    return Results.NotFound();
                }

                // Update the user
                existingUser.UserName = updatedUser.UserName;
                existingUser.UserAge = updatedUser.UserAge;

                return Results.NoContent();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return Results.Problem("An unexpected error occurred. Please try again later.");
            }
        });

        // Delete a user by username
        app.MapDelete("/users/{username}", (string username) =>
        {
            try
            {
                return users.TryRemove(username, out _) ? Results.NoContent() : Results.NotFound();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return Results.Problem("An unexpected error occurred. Please try again later.");
            }
        });

        app.Run();
    }
}


// Middleware for logging
public class LoggingMiddleware
{
    private readonly RequestDelegate _next;

    public LoggingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Log incoming request details
        Console.WriteLine($"Incoming Request: Method={context.Request.Method}, Path={context.Request.Path}");
        Console.WriteLine($"Headers: {string.Join(", ", context.Request.Headers.Select(h => $"{h.Key}: {h.Value}"))}");

        if (context.Request.ContentLength > 0 && context.Request.Body.CanSeek)
        {
            context.Request.Body.Position = 0;
            using (var reader = new StreamReader(context.Request.Body, leaveOpen: true))
            {
                var body = await reader.ReadToEndAsync();
                Console.WriteLine($"Request Body: {body}");
                context.Request.Body.Position = 0; // Reset the stream position
            }
        }

        // Capture the response
        var originalBodyStream = context.Response.Body;
        using (var responseBodyStream = new MemoryStream())
        {
            context.Response.Body = responseBodyStream;

            await _next(context);

            // Log outgoing response details
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            var responseBody = await new StreamReader(context.Response.Body).ReadToEndAsync();
            context.Response.Body.Seek(0, SeekOrigin.Begin);

            Console.WriteLine($"Outgoing Response: StatusCode={context.Response.StatusCode}");
            Console.WriteLine($"Response Body: {responseBody}");

            await responseBodyStream.CopyToAsync(originalBodyStream);
        }
    }
}
    
// Middleware for authentication
public class AuthenticationMiddleware
{
    private readonly RequestDelegate _next;

    public AuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Check for the Authorization header
        if (!context.Request.Headers.TryGetValue("Authorization", out var token))
        {
            context.Response.StatusCode = 401; // Unauthorized
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Authorization token is missing."
            });
            return;
        }
         string newToken = StringValues.IsNullOrEmpty(token) ? string.Empty : token.ToString();
        // Validate the token
        if (!string.IsNullOrEmpty(newToken))
        {        
            if (ValidateToken(newToken))
            {
                context.Response.StatusCode = 401; // Unauthorized
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Invalid or expired token."
                });
                return;
            }
        }
        // Proceed to the next middleware if the token is valid
        await _next(context);
    }

    private bool ValidateToken(string token)
    {
        // Example: Validate the token (replace this with your actual token validation logic)
        const string validToken = "Bearer my-secure-token"; // Replace with your token logic
        return token == validToken;
    }
}

// Middleware for error handling
public class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;

    public ErrorHandlingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            // Log the exception
            Console.WriteLine($"Unhandled Exception: {ex.Message}");
            Console.WriteLine($"Stack Trace: {ex.StackTrace}");

            // Return a standardized error response
            await HandleExceptionAsync(context, ex);
        }
    }

    private static Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        // Set response details
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;

        // Create a consistent error response
        var errorResponse = new
        {
            error = "Internal server error.",
            details = exception.Message // Optional: Remove in production for security reasons
        };

        // Serialize and return the error response
        return context.Response.WriteAsJsonAsync(errorResponse);
    }
}

// User model
public class User
{
    public string UserName { get; set; }
    public int UserAge { get; set; }
    public User(string userName, int userAge)
    {
        UserName = userName;
        UserAge = userAge;
    }
}

