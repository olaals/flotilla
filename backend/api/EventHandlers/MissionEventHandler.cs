﻿using Api.Controllers.Models;
using Api.Database.Models;
using Api.Services;
using Api.Services.Events;
using Api.Utilities;
namespace Api.EventHandlers
{

    public class MissionEventHandler : EventHandlerBase
    {
        private readonly ILogger<MissionEventHandler> _logger;

        // The mutex is used to ensure multiple missions aren't attempted scheduled simultaneously whenever multiple mission runs are created
        private readonly Semaphore _startMissionSemaphore = new(1, 1);
        private readonly Semaphore _scheduleLocalizationSemaphore = new(1, 1);

        private readonly IServiceScopeFactory _scopeFactory;

        public MissionEventHandler(
            ILogger<MissionEventHandler> logger,
            IServiceScopeFactory scopeFactory
        )
        {
            _logger = logger;
            _scopeFactory = scopeFactory;

            Subscribe();
        }
        private IMissionRunService MissionService => _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<IMissionRunService>();

        private IRobotService RobotService => _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<IRobotService>();

        private ILocalizationService LocalizationService => _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<ILocalizationService>();

        private IAreaService AreaService => _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<IAreaService>();

        private IMissionSchedulingService MissionScheduling => _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<IMissionSchedulingService>();

        private ISignalRService SignalRService => _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<ISignalRService>();

        private IReturnToHomeService ReturnToHomeService => _scopeFactory.CreateScope().ServiceProvider.GetRequiredService<IReturnToHomeService>();


        public override void Subscribe()
        {
            MissionRunService.MissionRunCreated += OnMissionRunCreated;
            MissionSchedulingService.RobotAvailable += OnRobotAvailable;
            MissionSchedulingService.LocalizationMissionSuccessful += OnLocalizationMissionSuccessful;
            EmergencyActionService.SendRobotToDockTriggered += OnSendRobotToDockTriggered;
            EmergencyActionService.ReleaseRobotFromDockTriggered += OnReleaseRobotFromDockTriggered;
        }

        public override void Unsubscribe()
        {
            MissionRunService.MissionRunCreated -= OnMissionRunCreated;
            MissionSchedulingService.RobotAvailable -= OnRobotAvailable;
            MissionSchedulingService.LocalizationMissionSuccessful -= OnLocalizationMissionSuccessful;
            EmergencyActionService.SendRobotToDockTriggered -= OnSendRobotToDockTriggered;
            EmergencyActionService.ReleaseRobotFromDockTriggered -= OnReleaseRobotFromDockTriggered;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await stoppingToken;
        }

        private async void OnMissionRunCreated(object? sender, MissionRunCreatedEventArgs e)
        {
            _logger.LogInformation("Triggered MissionRunCreated event for mission run ID: {MissionRunId}", e.MissionRunId);

            var missionRun = await MissionService.ReadById(e.MissionRunId, readOnly: true);
            if (missionRun == null)
            {
                _logger.LogError("Mission run with ID: {MissionRunId} was not found in the database", e.MissionRunId);
                return;
            }

            _scheduleLocalizationSemaphore.WaitOne();
            if (!await LocalizationService.RobotIsLocalized(missionRun.Robot.Id))
            {
                if (await MissionService.PendingLocalizationMissionRunExists(missionRun.Robot.Id)
                    || await MissionService.OngoingOrPausedLocalizationMissionRunExists(missionRun.Robot.Id))
                {
                    _scheduleLocalizationSemaphore.Release();
                    return;
                }
                _logger.LogInformation("{Message}", $"Changing mission run with ID {missionRun.Id} to localization type");
                await MissionService.UpdateMissionRunType(missionRun.Id, MissionRunType.Localization);
            }
            _scheduleLocalizationSemaphore.Release();

            _startMissionSemaphore.WaitOne();

            if (missionRun.MissionRunType != MissionRunType.ReturnHome && await ReturnToHomeService.GetActiveReturnToHomeMissionRun(missionRun.Robot.Id, readOnly: true) != null)
            {
                await MissionScheduling.AbortActiveReturnToHomeMission(missionRun.Robot.Id);
            }

            try { await MissionScheduling.StartNextMissionRunIfSystemIsAvailable(missionRun.Robot.Id); }
            catch (MissionRunNotFoundException) { return; }
            finally { _startMissionSemaphore.Release(); }
        }

        private async void OnRobotAvailable(object? sender, RobotAvailableEventArgs e)
        {
            _logger.LogInformation("Triggered RobotAvailable event for robot ID: {RobotId}", e.RobotId);
            var robot = await RobotService.ReadById(e.RobotId, readOnly: true);
            if (robot == null)
            {
                _logger.LogError("Robot with ID: {RobotId} was not found in the database", e.RobotId);
                return;
            }

            _startMissionSemaphore.WaitOne();
            try { await MissionScheduling.StartNextMissionRunIfSystemIsAvailable(robot.Id); }
            catch (MissionRunNotFoundException) { return; }
            finally { _startMissionSemaphore.Release(); }
        }

        private async void OnLocalizationMissionSuccessful(object? sender, LocalizationMissionSuccessfulEventArgs e)
        {
            _logger.LogInformation("Triggered LocalizationMissionSuccessful event for robot ID: {RobotId}", e.RobotId);
            var robot = await RobotService.ReadById(e.RobotId, readOnly: true);
            if (robot == null)
            {
                _logger.LogError("Robot with ID: {RobotId} was not found in the database", e.RobotId);
                return;
            }

            var lastMissionRun = await MissionService.ReadLastExecutedMissionRunByRobot(robot.Id, readOnly: true);
            if (lastMissionRun != null)
            {
                if (lastMissionRun.MissionRunType == MissionRunType.Emergency & lastMissionRun.Status == MissionStatus.Successful)
                {
                    _logger.LogInformation("Return to dock mission on robot {RobotName} was successful.", robot.Name);
                    SignalRService.ReportDockSuccessToSignalR(robot, $"Robot {robot.Name} is in the dock");
                }
            }

            _startMissionSemaphore.WaitOne();
            try { await MissionScheduling.StartNextMissionRunIfSystemIsAvailable(robot.Id); }
            catch (MissionRunNotFoundException) { return; }
            catch (DatabaseUpdateException) { return; }
            finally { _startMissionSemaphore.Release(); }
        }

        private async void OnSendRobotToDockTriggered(object? sender, RobotEmergencyEventArgs e)
        {
            _logger.LogInformation("Triggered EmergencyButtonPressed event for robot ID: {RobotId}", e.RobotId);
            var robot = await RobotService.ReadById(e.RobotId, readOnly: true);
            if (robot == null)
            {
                _logger.LogError("Robot with ID: {RobotId} was not found in the database", e.RobotId);
                return;
            }

            try { await MissionScheduling.FreezeMissionRunQueueForRobot(e.RobotId); }
            catch (RobotNotFoundException) { return; }

            if (robot.FlotillaStatus == e.RobotFlotillaStatus)
            {
                _logger.LogInformation("Did not send robot to Dock since robot {RobotId} was already in the correct state", e.RobotId);
                return;
            }

            try { await RobotService.UpdateFlotillaStatus(e.RobotId, e.RobotFlotillaStatus ?? RobotFlotillaStatus.Normal); }
            catch (Exception ex)
            {
                _logger.LogError("Was not able to update Robot Flotilla status for robot {RobotId}, {ErrorMessage}", e.RobotId, ex.Message);
                return;
            }

            Area? area;
            try
            {
                area = await FindRelevantRobotAreaForDockMission(robot.Id);
            }
            catch (RobotNotFoundException)
            {
                _logger.LogWarning(
                    "Failed to see if robot was localised. Could not find robot with ID '{RobotId}'",
                    e.RobotId
                );
                return;
            }

            if (area == null) { return; }

            try { await MissionScheduling.ScheduleMissionToDriveToDockPosition(e.RobotId, area.Id); }
            catch (DockException ex)
            {
                _logger.LogError(ex, "Failed to schedule return to dock mission on robot {RobotName} because: {ErrorMessage}", robot.Name, ex.Message);
                SignalRService.ReportDockFailureToSignalR(robot, $"Failed to send {robot.Name} to a dock");
            }

            if (await MissionService.PendingOrOngoingLocalizationMissionRunExists(e.RobotId)) { return; }
            try { await MissionScheduling.StopCurrentMissionRun(e.RobotId); }
            catch (RobotNotFoundException) { return; }
            catch (MissionRunNotFoundException)
            {
                /* Allow robot to return to dock if there is no ongoing mission */
            }
            catch (MissionException ex)
            {
                // We want to continue driving to the dock if the isar state is idle
                if (ex.IsarStatusCode != StatusCodes.Status409Conflict)
                {
                    _logger.LogError(ex, "Failed to stop the current mission on robot {RobotName} because: {ErrorMessage}", robot.Name, ex.Message);
                    SignalRService.ReportDockFailureToSignalR(robot, $"Failed to stop current mission for robot {robot.Name}");
                    return;
                }
            }
            catch (Exception ex)
            {
                const string Message = "Error in ISAR while stopping current mission, cannot drive to docking station.";
                SignalRService.ReportDockFailureToSignalR(robot, $"Robot {robot.Name} failed to drive to docking station.");
                _logger.LogError(ex, "{Message}", Message);
                return;
            }

            _startMissionSemaphore.WaitOne();
            try { await MissionScheduling.StartNextMissionRunIfSystemIsAvailable(robot.Id); }
            catch (MissionRunNotFoundException) { return; }
            finally { _startMissionSemaphore.Release(); }
        }

        private async void OnReleaseRobotFromDockTriggered(object? sender, RobotEmergencyEventArgs e)
        {
            _logger.LogInformation("Triggered EmergencyButtonPressed event for robot ID: {RobotId}", e.RobotId);
            var robot = await RobotService.ReadById(e.RobotId, readOnly: true);
            if (robot == null)
            {
                _logger.LogError("Robot with ID: {RobotId} was not found in the database", e.RobotId);
                return;
            }

            if (robot.FlotillaStatus == e.RobotFlotillaStatus)
            {
                _logger.LogInformation("Did not release robot from Dock since robot {RobotId} was already in the correct state", e.RobotId);
                return;
            }

            try { await MissionScheduling.UnfreezeMissionRunQueueForRobot(e.RobotId); }
            catch (RobotNotFoundException) { return; }

            try { await RobotService.UpdateFlotillaStatus(e.RobotId, e.RobotFlotillaStatus ?? RobotFlotillaStatus.Normal); }
            catch (Exception ex)
            {
                _logger.LogError("Was not able to update Robot Flotilla status for robot {RobotId}, {ErrorMessage}", e.RobotId, ex.Message);
                return;
            }

            _startMissionSemaphore.WaitOne();
            try { await MissionScheduling.StartNextMissionRunIfSystemIsAvailable(robot.Id); }
            catch (MissionRunNotFoundException) { return; }
            finally { _startMissionSemaphore.Release(); }
        }

        private async Task<Area?> FindRelevantRobotAreaForDockMission(string robotId)
        {
            var robot = await RobotService.ReadById(robotId, readOnly: true);
            if (robot == null)
            {
                _logger.LogError("Robot with ID: {RobotId} was not found in the database", robotId);
                return null;
            }

            if (!await LocalizationService.RobotIsLocalized(robotId))
            {

                if (await MissionService.PendingOrOngoingLocalizationMissionRunExists(robotId))
                {
                    var missionRuns = await MissionService.ReadAll(
                        new MissionRunQueryStringParameters
                        {
                            Statuses = [MissionStatus.Ongoing, MissionStatus.Pending],
                            RobotId = robot.Id,
                            OrderBy = "DesiredStartTime",
                            PageSize = 100
                        }, readOnly: true);

                    var localizationMission = missionRuns.Find(missionRun => missionRun.IsLocalizationMission());

                    return localizationMission?.Area ?? null;
                }

                return null;
            }

            var area = await AreaService.ReadById(robot.CurrentArea!.Id, readOnly: true);
            if (area == null)
            {
                _logger.LogError("Could not find area with ID {AreaId}", robot.CurrentArea!.Id);
                SignalRService.ReportDockFailureToSignalR(robot, $"Robot {robot.Name} was not correctly localised. Could not find area {robot.CurrentArea.Name}");
                return null;
            }

            return area;
        }


    }
}
