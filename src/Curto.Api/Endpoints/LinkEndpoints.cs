using System.Text.Json;
using Curto.Api.Infrastructure;
using Curto.Api.Models;
using Curto.Api.Services;

namespace Curto.Api.Endpoints;

/// <summary>Management endpoints for links: <c>/api/links</c>.</summary>
internal static class LinkEndpoints
{
    /// <summary>Maps the link endpoints below the (already authorised) API group.</summary>
    /// <param name="api">The <c>/api</c> group.</param>
    public static void MapLinkEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var links = api.MapGroup("/links").WithTags("Links");

        links.MapPost(string.Empty, CreateAsync)
            .WithName("CreateLink")
            .WithSummary("Create a short link")
            .WithDescription("Generates a random 7-character slug unless a custom one is given. Responds 201 with the link and its short URL.")
            .RequireRateLimiting(RateLimitPolicies.Create)
            .Produces<LinkResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        links.MapGet(string.Empty, ListAsync)
            .WithName("ListLinks")
            .WithSummary("List links")
            .WithDescription("Filter by text, tag and status; sort by creation time or clicks; page with limit and offset.")
            .Produces<LinkPage>()
            .ProducesValidationProblem();

        links.MapGet("/{slug:slug}", GetAsync)
            .WithName("GetLink")
            .WithSummary("Get one link")
            .Produces<LinkResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        links.MapPatch("/{slug:slug}", PatchAsync)
            .WithName("UpdateLink")
            .WithSummary("Update a link (JSON merge patch)")
            .WithDescription("Present fields change, null clears title, tags, expiresAt or maxClicks, absent fields stay. The slug cannot be changed.")
            .Accepts<LinkPatchRequest>("application/json", "application/merge-patch+json")
            .Produces<LinkResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        links.MapDelete("/{slug:slug}", DeleteAsync)
            .WithName("DeleteLink")
            .WithSummary("Delete a link and its analytics")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> CreateAsync(
        CreateLinkRequest request,
        HttpContext http,
        LinkRequestValidator validator,
        LinkService links,
        LinkMapper mapper,
        CancellationToken cancellationToken)
    {
        var validated = validator.ValidateCreate(request);
        if (!validated.IsValid)
        {
            return Problems.Validation(validated.Errors);
        }

        var result = await links.CreateAsync(validated.Value!, cancellationToken);
        return result.Failure switch
        {
            CreateFailure.None => TypedResults.Created($"/api/links/{result.Link!.Slug}", mapper.ToResponse(result.Link, http.Request)),
            CreateFailure.SlugTaken => Problems.SlugTaken(validated.Value!.Slug!),
            _ => Problems.ServiceUnavailable("Could not allocate a unique slug. Retry the request."),
        };
    }

    private static async Task<IResult> ListAsync(
        [AsParameters] LinkListParameters parameters,
        HttpContext http,
        LinkService links,
        LinkMapper mapper,
        CancellationToken cancellationToken)
    {
        var validated = LinkRequestValidator.ValidateQuery(parameters);
        if (!validated.IsValid)
        {
            return Problems.Validation(validated.Errors);
        }

        var (items, total) = await links.ListAsync(validated.Value!, cancellationToken);
        return TypedResults.Ok(new LinkPage([.. items.Select(link => mapper.ToResponse(link, http.Request))], total));
    }

    private static async Task<IResult> GetAsync(string slug, HttpContext http, LinkService links, LinkMapper mapper, CancellationToken cancellationToken)
    {
        var link = await links.GetAsync(slug, cancellationToken);
        return link is null ? NotFound(slug) : TypedResults.Ok(mapper.ToResponse(link, http.Request));
    }

    private static async Task<IResult> PatchAsync(
        string slug,
        JsonElement body,
        HttpContext http,
        LinkRequestValidator validator,
        LinkService links,
        LinkMapper mapper,
        CancellationToken cancellationToken)
    {
        var validated = validator.ValidatePatch(body);
        if (!validated.IsValid)
        {
            return Problems.Validation(validated.Errors);
        }

        var link = await links.UpdateAsync(slug, validated.Value!, cancellationToken);
        return link is null ? NotFound(slug) : TypedResults.Ok(mapper.ToResponse(link, http.Request));
    }

    private static async Task<IResult> DeleteAsync(string slug, LinkService links, CancellationToken cancellationToken) =>
        await links.DeleteAsync(slug, cancellationToken) ? TypedResults.NoContent() : NotFound(slug);

    private static IResult NotFound(string slug) => Problems.NotFound($"No link with slug '{slug}'.");
}
