namespace Data.Connector.Svc;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Iot.Operations.Mqtt.Session;
using Azure.Iot.Operations.Protocol.Connection;
using Azure.Iot.Operations.Protocol.Models;
using MQTTnet.Exceptions;

public class MqttDataSink : IDataSink
{
    private readonly ILogger _logger;
    private readonly MqttSessionClient _mqttSessionClient;

    private readonly string _host;

    private readonly int _port;

    private readonly string? _clientId;

    private readonly bool _useTls;

    private readonly string _username;

    private readonly string _passwordFilePath;

    private readonly string _satFilePath;

    private readonly string _caFilePath;

    private readonly string _topic;

    private readonly string _sourceId;

    private int _initialBackoffDelayInMilliseconds;

    private int _maxBackoffDelayInMilliseconds;

    private StringReplacement[] _topicStringReplacements;

    public MqttDataSink(
        ILogger logger,
        MqttSessionClient mqttSessionClient,
        string host,
        int port,
        string? clientId,
        bool useTls,
        string username,
        string passwordFilePath,
        string satFilePath,
        string caFilePath,
        string baseTopic,
        string sourceId,
        StringReplacement[] topicStringReplacements,
        int initialBackoffDelayInMilliseconds = 500,
        int maxBackoffDelayInMilliseconds = 10_000)
    {
        _logger = logger;
        _mqttSessionClient = mqttSessionClient ?? throw new ArgumentNullException(nameof(mqttSessionClient));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _port = port;
        _clientId = clientId ?? Guid.NewGuid().ToString();
        _useTls = useTls;
        _username = username;
        _passwordFilePath = passwordFilePath;
        _satFilePath = satFilePath;
        _caFilePath = caFilePath;
        _sourceId = sourceId ?? throw new ArgumentNullException(nameof(sourceId));
        _initialBackoffDelayInMilliseconds = initialBackoffDelayInMilliseconds;
        _maxBackoffDelayInMilliseconds = maxBackoffDelayInMilliseconds;
        _topicStringReplacements = topicStringReplacements;

        // Generate the topic name for MQTT sink
        ArgumentNullException.ThrowIfNull(baseTopic);
        _topic = GenerateTopicName(baseTopic);

        // Set the unique identifier for the data sink for observability.
        Id = $"{_clientId}-{_host}-{port}-{_topic}";
    }

    public string Id { get; init; }

    public async Task PushDataAsync(JsonDocument data, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        var mqtt_application_message = new MqttApplicationMessage(_topic, MqttQualityOfServiceLevel.AtLeastOnce)
        {
            PayloadSegment = new ArraySegment<byte>(Encoding.UTF8.GetBytes(data.RootElement.GetRawText())),
            UserProperties = new List<MqttUserProperty>
            {
                // TODO: property name should be qualified better i.e. "connector can be any connector type"
                new MqttUserProperty("connector-source-id", _sourceId)
            }
        };

        // Publish data to the MQTT broker until successful.
        var successfulPublish = false;
        int backoff_delay_in_milliseconds = _initialBackoffDelayInMilliseconds;
        while (!successfulPublish)
        {
            try
            {
                await _mqttSessionClient.PublishAsync(mqtt_application_message, stoppingToken);
                _logger.LogTrace("Published data to MQTT broker, topic: '{topic}'.", _topic);

                // Reset backoff delay on successful data processing.
                backoff_delay_in_milliseconds = _initialBackoffDelayInMilliseconds;
                successfulPublish = true;
            }
            catch (MqttCommunicationException ex)
            {
                _logger.LogError(ex, "Error publishing data to MQTT broker, topic: '{topic}', reconnecting...", _topic);

                await Task.Delay(backoff_delay_in_milliseconds);
                backoff_delay_in_milliseconds = (int)Math.Pow(backoff_delay_in_milliseconds, 1.02);

                // Limit backoff delay to _maxBackoffDelayInMilliseconds.
                backoff_delay_in_milliseconds = backoff_delay_in_milliseconds > _maxBackoffDelayInMilliseconds ? _maxBackoffDelayInMilliseconds : backoff_delay_in_milliseconds;

                await _mqttSessionClient.ReconnectAsync();
            }
        }
    }

    public void Connect()
    {
        if (!_mqttSessionClient.IsConnected)
        {
            _logger.LogInformation("MQTT SAT token file location: '{file}'.", _satFilePath);
            _logger.LogInformation("CA cert file location: '{file}'.", _caFilePath);
            _logger.LogInformation("Password file location: '{file}'.", _passwordFilePath);

            MqttConnectionSettings connectionSettings = new(_host)
            {
                TcpPort = _port,
                ClientId = _clientId,
                UseTls = _useTls,
                Username = _username,
                PasswordFile = _passwordFilePath,
                SatAuthFile = _satFilePath,
                CaFile = _caFilePath
            };

            var result_code = _mqttSessionClient.ConnectAsync(connectionSettings)
                                .ConfigureAwait(false)
                                .GetAwaiter()
                                .GetResult();

            if (result_code.ResultCode != MqttClientConnectResultCode.Success)
            {
                throw new ApplicationException($"Failed to connect to the MQTT broker, code {result_code}");
            }
        }
    }

    private string GenerateTopicName(string baseTopic)
    {
        string topic = _sourceId;

        // Hash the source id to create a unique topic name.
        using SHA256 sha256Hash = SHA256.Create();
        var bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(_sourceId));
        var hash = new StringBuilder();

        for (int i = 0; i < bytes.Length; i++)
        {
            hash.Append(bytes[i].ToString("x2"));
        }

        // Replace invalid strings in the topic name if configured.
        if (_topicStringReplacements is not null)
        {
            foreach (var replacement in _topicStringReplacements)
            {
                topic = topic.Replace(replacement.OldValue, replacement.NewValue, StringComparison.InvariantCultureIgnoreCase);
            }
        }

        return $"{baseTopic}{hash.ToString()}/{topic}";
    }
}
