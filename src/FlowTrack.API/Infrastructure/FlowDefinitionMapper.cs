using System.Text.Json;
using AutoMapper;
using FlowTrack.Application;
using FlowTrack.Domain;

namespace FlowTrack.API.Infrastructure;

internal static class FlowDefinitionMapper
{
    public static FlowDto ToDto(FlowDefinition flow, IMapper mapper, bool includeTokenValues = false, bool hasDraft = false)
    {
        return new FlowDto(
            flow.Id,
            flow.FlowKey,
            flow.Name,
            flow.Description,
            flow.Active,
            flow.VersionNumber,
            flow.LifecycleStatus.ToString(),
            flow.PublishedAt,
            hasDraft,
            flow.Tokens
                .OrderBy(x => x.Name)
                .Select(x => new FlowTokenDto(x.Id, x.Name, includeTokenValues ? x.Value : null, x.Type, x.HeaderName, x.Active))
                .ToList(),
            flow.Steps
                .OrderBy(x => x.Order)
                .Select(step => new StepDto(
                    step.Id,
                    step.Name,
                    step.Description,
                    step.Type,
                    step.Order,
                    step.AssignedUserId,
                    step.Fields
                        .OrderBy(x => x.Order)
                        .Select(mapper.Map<FieldDto>)
                        .ToList(),
                    ParseApiConfig(step.ConfigurationJson)))
                .ToList());
    }

    public static void Apply(FlowDefinition flow, SaveFlowRequest request)
    {
        flow.Name = request.Name.Trim();
        flow.Description = request.Description.Trim();
        flow.Active = request.Active;

        flow.Tokens = request.Tokens
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new FlowToken
            {
                Name = x.Name.Trim(),
                Value = x.Value?.Trim() ?? string.Empty,
                Type = x.Type,
                HeaderName = string.IsNullOrWhiteSpace(x.HeaderName) ? null : x.HeaderName.Trim(),
                Active = x.Active
            })
            .ToList();

        flow.Steps = request.Steps
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select((step, stepIndex) => new FlowStep
            {
                Name = step.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(step.Description) ? null : step.Description.Trim(),
                Type = step.Type,
                Order = stepIndex + 1,
                AssignedUserId = step.AssignedUserId,
                ConfigurationJson = SerializeApiConfig(step.ApiConfig),
                Fields = step.Fields
                    .Where(x => !string.IsNullOrWhiteSpace(x.Label) && !string.IsNullOrWhiteSpace(x.Key))
                    .Select((field, fieldIndex) => new StepField
                    {
                        Key = field.Key.Trim(),
                        Label = field.Label.Trim(),
                        Type = field.Type,
                        Required = field.Required,
                        Order = fieldIndex + 1,
                        Options = field.Options
                            .Where(x => !string.IsNullOrWhiteSpace(x.Label) || !string.IsNullOrWhiteSpace(x.Value))
                            .Select((option, optionIndex) => new StepFieldOption
                            {
                                Label = string.IsNullOrWhiteSpace(option.Label) ? option.Value.Trim() : option.Label.Trim(),
                                Value = string.IsNullOrWhiteSpace(option.Value) ? option.Label.Trim() : option.Value.Trim(),
                                Order = optionIndex + 1
                            })
                            .ToList()
                    })
                    .ToList()
            })
            .ToList();
    }

    public static string? SerializeApiConfig(StepApiConfigDto? config)
    {
        if (config is null)
        {
            return null;
        }

        return JsonSerializer.Serialize(config);
    }

    public static StepApiConfigDto? ParseApiConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<StepApiConfigDto>(json);
    }
}
