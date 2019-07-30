﻿using Automatica.Core.Slave.Abstraction;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Diagnostics;
using MQTTnet.Server;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Timers.Timer;

namespace Automatica.Core.Slave.Runtime
{
    public class SlaveRuntime : IHostedService
    {
        private readonly IMqttClientOptions _options;
        private readonly IMqttClient _mqttClient;
        private readonly DockerClient _dockerClient;

        private readonly IDictionary<string, string> _runningImages = new Dictionary<string, string>();

        private readonly string _slaveId;

        private readonly string _masterAddress;
        private readonly string _clientKey;
        private readonly ILogger _logger;


        private readonly Timer _timer = new Timer(5000);

        public SlaveRuntime(IServiceProvider services, ILogger<SlaveRuntime> logger)
        {
            var config = services.GetRequiredService<IConfiguration>();

            _logger = logger;

            _masterAddress = config["server:master"];
            _clientKey = config["server:clientKey"];

            _slaveId = config["server:clientId"];
            _options = new MqttClientOptionsBuilder()
                   .WithClientId(_slaveId)
                   //   .WithWebSocketServer("localhost:5001/mqtt")
                   .WithTcpServer(_masterAddress, 1883)
                   .WithCredentials(_slaveId, _clientKey)
                   .WithCleanSession()
                   .Build();
            if (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable("MQTT_LOG_VERBOSE")))
            {

                MqttNetGlobalLogger.LogMessagePublished += (s, e) =>
                {
                    var trace =
                        $">> [{e.TraceMessage.Timestamp:O}] [{e.TraceMessage.ThreadId}] [{e.TraceMessage.Source}] [{e.TraceMessage.Level}]: {e.TraceMessage.Message}";
                    if (e.TraceMessage.Exception != null)
                    {
                        trace += Environment.NewLine + e.TraceMessage.Exception.ToString();
                    }

                    _logger.LogTrace(trace);
                };
            }

            _mqttClient = new MqttFactory().CreateMqttClient();

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    _dockerClient = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    _dockerClient = new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine")).CreateClient();
                }
                else
                {
                    throw new PlatformNotSupportedException();
                }
            }
            catch (PlatformNotSupportedException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Could not connect do docker daemon!");
            }


            _timer.Elapsed += _timer_Elapsed;

        }

        private async void _timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (!_mqttClient.IsConnected)
            {
                await StopInternal();
                await StartInternal();
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await StartInternal();
            _timer.Start();
        }

        private async Task StartInternal()
        {
            try
            {
                await _dockerClient.Images.ListImagesAsync(new ImagesListParameters());

                await _mqttClient.ConnectAsync(_options);

                var topic = $"slave/{_slaveId}/action";
                var topics = $"slave/{_slaveId}/actions";

                await _mqttClient.SubscribeAsync(new TopicFilterBuilder().WithTopic(topic).WithExactlyOnceQoS().Build());
                await _mqttClient.SubscribeAsync(new TopicFilterBuilder().WithTopic(topics).WithExactlyOnceQoS().Build());


                _mqttClient.ApplicationMessageReceived += async (sender, e) =>
                {
                    _logger.LogDebug($"Mqtt message received for topic {e.ApplicationMessage.Topic}");

                    if (MqttTopicFilterComparer.IsMatch(topic, e.ApplicationMessage.Topic))
                    {
                        var json = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                        var action = JsonConvert.DeserializeObject<ActionRequest>(json);
                        try
                        {
                            await ExecuteAction(action);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Could not execute request");
                        }
                    }
                    else if (MqttTopicFilterComparer.IsMatch(topics, e.ApplicationMessage.Topic))
                    {
                        var json = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                        var actions = JsonConvert.DeserializeObject<ActionRequest[]>(json);
                        try
                        {
                            foreach (var action in actions)
                            {
                                await ExecuteAction(action);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Could not execute request");
                        }
                    }
                };

            }
            catch (HttpRequestException e)
            {
                _logger.LogError(e, $"Error connecting to docker process...");
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error starting slave process...");
            }
        }

        private async Task ExecuteAction(ActionRequest action)
        {
            switch (action.Action)
            {
                case SlaveAction.Start:
                    await StartImage(action.ImageSource, action.ImageName, action.Tag);
                    break;
                case SlaveAction.Stop:
                    await StopImage(action.ImageName, action.Tag);
                    break;
            }
        }

        private async Task StopImage(string imageName, string imageTag)
        {
            var imageFullName = $"{imageName}:{imageTag}";
            _logger.LogInformation($"Stop Image {imageFullName}");

            if (_runningImages.ContainsKey(imageFullName))
            {
                await _dockerClient.Containers.StopContainerAsync(_runningImages[imageFullName], new ContainerStopParameters());
                try
                {
                    await _dockerClient.Images.DeleteImageAsync(imageFullName, new ImageDeleteParameters
                    {
                        Force = true
                    });
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Could not delete image");
                }

                _runningImages.Remove(imageFullName);
            }
            else
            {
                _logger.LogError($"Could not stop image, image {imageName}{imageTag} not found");
            }
        }

        private async Task StartImage(string imageSource, string imageName, string imageTag)
        {
            var imageFullName = $"{imageName}:{imageTag}";
            _logger.LogInformation($"Start Image {imageFullName}");

            var imageCreateParams = new ImagesCreateParameters()
            {
                FromImage = imageName,
                Tag = imageTag
            };

            if (!String.IsNullOrEmpty(imageSource))
            {
                imageCreateParams.FromSrc = imageSource;
            }

            await _dockerClient.Images.CreateImageAsync(imageCreateParams, new AuthConfig(), new ImageProgress(_logger));


            try
            {
                var portBindings = new Dictionary<string, IList<PortBinding>>();
                portBindings.Add("1883/tcp", new List<PortBinding>()
                {
                    new PortBinding()
                    {
                        HostPort = "1883"
                    }
                });

                var createContainerParams = new CreateContainerParameters()
                {
                    Image = imageFullName,
                    AttachStderr = false,
                    AttachStdin = false,
                    AttachStdout = false,
                    HostConfig = new HostConfig
                    {
                        PortBindings = portBindings
                    },
                    Env = new[] { $"AUTOMATICA_SLAVE_MASTER={_masterAddress}", $"AUTOMATICA_SLAVE_USER={_slaveId}", $"AUTOMATICA_SLAVE_PASSWORD={_clientKey}" },
                };

                createContainerParams.HostConfig.NetworkMode = "host";

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                    RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    createContainerParams.HostConfig.Privileged = true;

                    createContainerParams.HostConfig.Mounts.Add(new Mount
                    {
                        Source = "/dev",
                        Target = "/dev",
                        Type = "bind"
                    });

                    createContainerParams.HostConfig.Mounts.Add(new Mount
                    {
                        Source = "/tmp",
                        Target = "/tmp",
                        Type = "bind"
                    });
                }

                var response = await _dockerClient.Containers.CreateContainerAsync(createContainerParams);


                if (_runningImages.ContainsKey(imageFullName))
                {
                    _runningImages.Remove(imageFullName);
                }
                _runningImages.Add(imageFullName, response.ID);
                await _dockerClient.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());

            }
            catch (Exception e)
            {
                _logger.LogError(e, "error starting image...");
            }

        }

        private async Task StopInternal()
        {
            try
            {
                foreach (var id in _runningImages)
                {
                    await _dockerClient.Containers.StopContainerAsync(id.Value, new ContainerStopParameters());
                }

                _runningImages.Clear();
                await _mqttClient.DisconnectAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error stopping connection...");
            }
        }


        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await StopInternal();


            _timer.Elapsed -= _timer_Elapsed;
        }
    }
}
