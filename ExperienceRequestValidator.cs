using OpenMetaverse;

namespace Munibot;

public static class ExperienceRequestValidator
{
    public static UUID NormalizeExperienceId(string experienceUuid)
    {
        var value = experienceUuid?.Trim();
        if (string.IsNullOrWhiteSpace(value) ||
            !UUID.TryParse(value, out var experienceId) ||
            experienceId == UUID.Zero)
        {
            throw new ArgumentException("A valid Second Life experience UUID is required.");
        }

        return experienceId;
    }
}
