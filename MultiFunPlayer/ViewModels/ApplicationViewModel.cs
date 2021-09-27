﻿using MultiFunPlayer.Common;
using MultiFunPlayer.Common.Messages;
using Newtonsoft.Json.Linq;
using Stylet;
using System.IO;
using System.Linq;
using System.Windows;

namespace MultiFunPlayer.ViewModels
{
    public class ApplicationViewModel : Screen, IHandle<AppSettingsMessage>
    {
        public BindableCollection<string> DeviceTypes { get; }
        public string SelectedDevice { get; set; }
        public bool AlwaysOnTop { get; set; }

        public ApplicationViewModel(IEventAggregator eventAggregator)
        {
            eventAggregator.Subscribe(this);

            var devices = JObject.Parse(File.ReadAllText("MultiFunPlayer.device.json")).Properties().Select(p => p.Name);
            DeviceTypes = new BindableCollection<string>(devices);
        }

        public void OnAlwaysOnTopChanged()
        {
            Application.Current.MainWindow.Topmost = AlwaysOnTop;
        }

        public void Handle(AppSettingsMessage message)
        {
            if (message.Type == AppSettingsMessageType.Saving)
            {
                message.Settings[nameof(SelectedDevice)] = SelectedDevice;
                message.Settings[nameof(AlwaysOnTop)] = AlwaysOnTop;
            }
            else if (message.Type == AppSettingsMessageType.Loading)
            {
                if(message.Settings.TryGetValue<string>(nameof(SelectedDevice), out var selectedDevice))
                    SelectedDevice = selectedDevice;

                if (message.Settings.TryGetValue<bool>(nameof(AlwaysOnTop), out var alwaysOnTop))
                    AlwaysOnTop = alwaysOnTop;
            }
        }
    }
}
