﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Fizzi.Applications.ChallongeVisualization.Model;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Threading;
using Fizzi.Applications.ChallongeVisualization.Common;
using System.Windows.Input;
using Fizzi.Libraries.ChallongeApiWrapper;
using System.IO;

namespace Fizzi.Applications.ChallongeVisualization.ViewModel
{
    class OrganizerViewModel : INotifyPropertyChanged, IDisposable
    {
        public MainViewModel Mvm { get; private set; }

        public ObservableCollection<DisplayMatch> OpenMatches { get; private set; }
        public ObservableCollection<Station> OpenStations { get; private set; }

        private ObservableMatch _selectedMatch;
        public ObservableMatch SelectedMatch { get { return _selectedMatch; } set { this.RaiseAndSetIfChanged("SelectedMatch", ref _selectedMatch, value, PropertyChanged); } }

        private Station _selectedStation;
        public Station SelectedStation { get { return _selectedStation; } set { this.RaiseAndSetIfChanged("SelectedStation", ref _selectedStation, value, PropertyChanged); } }

        private IDisposable matchesMonitoring;
        private IDisposable matchStateMonitoring;
        private IDisposable stationMonitoring;

        public ICommand ImportStationFile { get; private set; }

        public ICommand AutoAssignPending { get; private set; }
        public ICommand CallPendingAnywhere { get; private set; }
        public ICommand ClearAllAssignments { get; private set; }

        public OrganizerViewModel(MainViewModel mvm)
        {
            Mvm = mvm;

            OpenMatches = new ObservableCollection<DisplayMatch>();
            OpenStations = new ObservableCollection<Station>();

            var mvmPropertyChanged = Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(h => mvm.PropertyChanged += h, h => mvm.PropertyChanged -= h);

            //Monitor if matches change (for example on a bracket reset)
            matchesMonitoring = mvmPropertyChanged.Where(ep => ep.EventArgs.PropertyName == "DisplayMatches")
                .Select(_ => System.Reactive.Unit.Default).StartWith(System.Reactive.Unit.Default)
                .ObserveOnDispatcher().Subscribe(_ => initialize(mvm.DisplayMatches.Where(dm => dm.MatchDisplayType == DisplayMatch.DisplayType.Assigned).ToArray()));

            ImportStationFile = Command.Create<System.Windows.Window>(_ => true, window =>
            {
                try
                {
                    var ofd = new Microsoft.Win32.OpenFileDialog()
                    {
                        Filter = "Text List (*.csv;*.txt)|*.csv;*.txt|All files (*.*)|*.*",
                        RestoreDirectory = true,
                        Title = "Browse for Station File"
                    };

                    var result = ofd.ShowDialog(window);
                    if (result.HasValue && result.Value)
                    {
                        var path = ofd.FileName;

                        initializeStations(path);
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(window, "Error encountered trying to import station list: " + ex.NewLineDelimitedMessages(), 
                        "Error Importing", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            });

            AutoAssignPending = Command.Create(() => true, () =>
            {
                Stations.Instance.AssignOpenMatchesToStations(OpenMatches.Select(dm => dm.Match).ToArray());
            });

            CallPendingAnywhere = Command.Create(() => true, () =>
            {
                var stationsWithoutAssignments = OpenMatches.Where(m => !m.Match.IsMatchInProgress).ToArray();

                foreach (var s in stationsWithoutAssignments) s.Match.AssignPlayersToStation("Any");
            });

            ClearAllAssignments = Command.Create(() => true, () =>
            {
                var stationsWithAssignments = OpenMatches.Where(m => m.Match.IsMatchInProgress).ToArray();

                foreach (var s in stationsWithAssignments) s.Match.ClearStationAssignment();
            });
        }

        private void initialize(DisplayMatch[] matches)
        {
            OpenMatches.Clear();

            if (matchStateMonitoring != null) matchStateMonitoring.Dispose();

            var subscriptions = matches.Select(dm =>
            {
                var m = dm.Match;
                var matchPropChanged = Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(h => m.PropertyChanged += h, h => m.PropertyChanged -= h);

                return matchPropChanged.Where(ep => ep.EventArgs.PropertyName == "State")
                    .Select(_ => System.Reactive.Unit.Default).StartWith(System.Reactive.Unit.Default)
                    .ObserveOnDispatcher().Subscribe(_ =>
                    {
                        if (m.State == "open") OpenMatches.Add(dm);
                        else OpenMatches.Remove(dm);
                    });
            }).ToArray();

            matchStateMonitoring = new CompositeDisposable(subscriptions);
        }

        private void initializeStations(Station[] uniqueStations)
        {
            //Load stations
            OpenStations.Clear();

            if (stationMonitoring != null) stationMonitoring.Dispose();

            //Start by adding all stations as open stations to the collection
            foreach (var s in uniqueStations) OpenStations.Add(s);

            //Load up Stations instance with new stations
            var stations = Stations.Instance;
            stations.LoadNew(uniqueStations);

            //Hook up station status change monitoring events for all stations
            var allStations = stations.Dict;
            var subscriptions = allStations.Select(kvp => kvp.Value).Select(s =>
            {
                var stationPropChanged = Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(h => s.PropertyChanged += h, h => s.PropertyChanged -= h);

                return stationPropChanged.Where(ep => ep.EventArgs.PropertyName == "Status")
                    .ObserveOnDispatcher().Subscribe(_ =>
                    {
                        if (s.Status == StationStatus.Open) OpenStations.Add(s);
                        else OpenStations.Remove(s);
                    });
            }).ToArray();

            //Get the names of all currently "in use" stations and mark them as in use, removing them from the observable collection via the event listener that was just hooked up
            var inUseStations = OpenMatches.Select(m => m.Match.StationAssignment).Where(sn => sn != null);
            foreach (var sn in inUseStations)
            {
                stations.AttemptClaimStation(sn);
            }

            stationMonitoring = new CompositeDisposable(subscriptions);
        }

        private void initializeStations(string filePath)
        {
            //Get station names from file
            var lines = File.ReadAllLines(filePath);

            //Only allow distinct station names and dont allow any station called "Any" as that is a reserved name
            var uniqueStations = lines.Select(line =>
            {
                var commaSeparated = line.Split(',');
                
                var name = commaSeparated.Length == 0 ? string.Empty : commaSeparated[0];

                StationType type = StationType.Standard;
                if (commaSeparated.Length > 1)
                {
                    var stationText = commaSeparated[1].Trim().ToLower();

                    if (stationText == "stream") type = StationType.Stream;
                    else if (stationText == "recording") type = StationType.Recording;
                    else if (stationText == "premium") type = StationType.Premium;
                    else if (stationText == "backup") type = StationType.Backup;
                    else if (stationText == "noassign") type = StationType.NoAssign;
                }

                return new { Name = name, Type = type };
            }).GroupBy(a => a.Name).Select(g => g.First()).Where(a => 
            {
                var trimmedName = a.Name.Trim().ToLower();

                return trimmedName != "any" && !string.IsNullOrWhiteSpace(trimmedName);
            }).Select((a, i) => new Station(a.Name, i, a.Type)).ToArray();

            //Initialize stations
            initializeStations(uniqueStations);
        }

        public void Dispose()
        {
            if (matchStateMonitoring != null) matchStateMonitoring.Dispose();
            if (matchesMonitoring != null) matchesMonitoring.Dispose();
            if (stationMonitoring != null) stationMonitoring.Dispose();
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
