namespace Orkka.Tests.Features.Agents;

using FluentValidation.TestHelper;
using Orkka.Modules.Agents.Features.Agents;

public sealed class AgentValidatorsTests
{
    private readonly UpdateAgentValidator _updateValidator = new();

    // The cap mirrors the pipeline prompt budget (instructions 4k) — text past it would
    // be silently truncated at execution time.
    [Fact]
    public void Update_UserInstructionsOverCap_Fails()
    {
        var request = new UpdateAgentRequest(Name: null, Avatar: null, Model: null,
            UserInstructions: new string('a', 4_001));
        _updateValidator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.UserInstructions);
    }

    [Fact]
    public void Update_UserInstructionsAtCap_Passes()
    {
        var request = new UpdateAgentRequest(Name: null, Avatar: null, Model: null,
            UserInstructions: new string('a', 4_000));
        _updateValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Update_EmptyName_Fails()
    {
        var request = new UpdateAgentRequest(Name: "", Avatar: null, Model: null, UserInstructions: null);
        _updateValidator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Update_AllNull_Passes()
    {
        var request = new UpdateAgentRequest(null, null, null, null);
        _updateValidator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }
}
