



using EZPannel.Api.Data;

public class ConfigService {

    public const string key_SystemName = "SystemName";
    public const string key_ProxyServerUrl = "ProxyServerUrl";

    private readonly AppDbContext _appContext;
    public ConfigService(AppDbContext appContext) {
        _appContext = appContext;
    }

    public string GetConfig(string key) {
        return _appContext.Configs.Where(x=>x.Key == key).FirstOrDefault()?.Value;
    }


    public string TryGetConfig(string key, string defaultValue = "") {
        return _appContext.Configs.Where(x=>x.Key == key).FirstOrDefault()?.Value ?? defaultValue;
    }

    public void SetConfig(string key,string val) {
        var config = _appContext.Configs.Where(x=>x.Key == key).FirstOrDefault();
        if (config == null) {
            config = new ConfigModel {
                Key = key,
                Value = val
            };
            _appContext.Configs.Add(config);
        }
        else {
            config.Value = val;
        }
        _appContext.SaveChanges();
    }
}