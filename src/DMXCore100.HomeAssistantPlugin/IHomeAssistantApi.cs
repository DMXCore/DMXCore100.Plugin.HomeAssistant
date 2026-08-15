namespace DMXCore100.HomeAssistantPlugin;

/// <summary>
/// Home Assistant client used to list and activate scenes. Production uses
/// REST; tests inject a fake.
/// </summary>
internal interface IHomeAssistantApi : IDisposable
{
    Task<IReadOnlyList<HomeAssistantScene>> GetScenesAsync(CancellationToken cancellationToken);

    Task ActivateSceneAsync(string entityId, CancellationToken cancellationToken);
}

internal delegate IHomeAssistantApi HomeAssistantApiFactory(
    string baseUrl,
    string accessToken,
    bool ignoreCertificates);
