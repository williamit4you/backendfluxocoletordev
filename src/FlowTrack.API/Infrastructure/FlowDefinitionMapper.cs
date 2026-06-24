using System.Text.Json;
using AutoMapper;
using FlowTrack.Application;
using FlowTrack.Domain;

namespace FlowTrack.API.Infrastructure;

internal static class FlowDefinitionMapper
{
    public static FlowDto ToDto(FlowDefinition flow, IMapper mapper, ITokenProtectionService tokenProtection, bool includeTokenValues = false, bool hasDraft = false)
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
                .Select(x => new FlowTokenDto(x.Id, x.Name, includeTokenValues && !string.IsNullOrWhiteSpace(x.Value) ? tokenProtection.Unprotect(x.Value) : null, x.Type, x.HeaderName, x.Active))
                .ToList(),
            flow.AssignedUsers
                .OrderBy(x => x.UserId)
                .Select(x => x.UserId)
                .ToList(),
            flow.Steps
                .OrderBy(x => x.Order)
                .Select(step => new StepDto(
                    step.Id,
                    step.Name,
                    step.Description,
                    step.Type,
                    step.Order,
                    step.AssignedUsers.OrderBy(x => x.UserId).Select(x => x.UserId).ToList(),
                    step.Fields
                        .OrderBy(x => x.Order)
                        .Select(field => new FieldDto(
                            field.Id,
                            field.Key,
                            field.Label,
                            field.Type,
                            field.Mask,
                            field.Required,
                            field.Order,
                            field.Options
                                .OrderBy(option => option.Order)
                                .Select(option => new FieldOptionDto(option.Id, option.Label, option.Value, option.Order, option.Key, option.Type, option.Mask, option.Required))
                                .ToList()))
                        .ToList(),
                    ParseApiConfig(step.ConfigurationJson)))
                .ToList());
    }

    public static void Apply(FlowDefinition flow, SaveFlowRequest request, ITokenProtectionService tokenProtection)
    {
        flow.Name = request.Name.Trim();
        flow.Description = request.Description.Trim();
        flow.Active = request.Active;

        flow.Tokens = request.Tokens
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new FlowToken
            {
                Name = x.Name.Trim(),
                Value = string.IsNullOrWhiteSpace(x.Value) ? string.Empty : tokenProtection.Protect(x.Value.Trim()),
                Type = x.Type,
                HeaderName = string.IsNullOrWhiteSpace(x.HeaderName) ? null : x.HeaderName.Trim(),
                Active = x.Active
            })
            .ToList();

        flow.AssignedUsers = request.AssignedUserIds
            .Distinct()
            .Select(userId => new FlowDefinitionUser
            {
                UserId = userId
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
                AssignedUserId = step.AssignedUserIds.FirstOrDefault(),
                AssignedUsers = step.AssignedUserIds
                    .Distinct()
                    .Select(userId => new FlowStepUser
                    {
                        UserId = userId
                    })
                    .ToList(),
                ConfigurationJson = SerializeApiConfig(step.ApiConfig),
                Fields = step.Fields
                    .Where(x => !string.IsNullOrWhiteSpace(x.Label) && !string.IsNullOrWhiteSpace(x.Key))
                    .Select((field, fieldIndex) => new StepField
                    {
                        Key = field.Key.Trim(),
                        Label = field.Label.Trim(),
                        Type = field.Type,
                        Mask = string.IsNullOrWhiteSpace(field.Mask) ? null : field.Mask.Trim(),
                        Required = field.Required,
                        Order = fieldIndex + 1,
                        Options = field.Options
                            .Where(x => !string.IsNullOrWhiteSpace(x.Label) || !string.IsNullOrWhiteSpace(x.Value) || !string.IsNullOrWhiteSpace(x.Key) || x.Type.HasValue)
                            .Select((option, optionIndex) => new StepFieldOption
                            {
                                Label = string.IsNullOrWhiteSpace(option.Label) ? option.Value.Trim() : option.Label.Trim(),
                                Value = string.IsNullOrWhiteSpace(option.Value) ? option.Label.Trim() : option.Value.Trim(),
                                Key = string.IsNullOrWhiteSpace(option.Key) ? null : option.Key.Trim(),
                                Type = option.Type,
                                Mask = string.IsNullOrWhiteSpace(option.Mask) ? null : option.Mask.Trim(),
                                Required = option.Required ?? false,
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
