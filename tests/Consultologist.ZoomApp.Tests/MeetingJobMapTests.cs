using Xunit;

using Consultologist.ZoomApp.Core.Meetings;

namespace Consultologist.ZoomApp.Tests;

public class MeetingJobMapTests
{
    private const string Clinician = "clinician-1";
    private const string Meeting = "abc==/meeting//uuid";

    [Fact]
    public async Task RecordsAndReturnsAMeetingsJobs_NewestFirst()
    {
        var map = new InMemoryMeetingJobMap();
        await map.RecordAsync(Clinician, Meeting, "job-1");
        await map.RecordAsync(Clinician, Meeting, "job-2");

        Assert.Equal(new[] { "job-2", "job-1" }, await map.JobsForAsync(Clinician, Meeting));
    }

    [Fact]
    public async Task RecordingTheSameJobTwice_IsIdempotent_AndMovesItToTheFront()
    {
        var map = new InMemoryMeetingJobMap();
        await map.RecordAsync(Clinician, Meeting, "job-1");
        await map.RecordAsync(Clinician, Meeting, "job-2");
        await map.RecordAsync(Clinician, Meeting, "job-1");

        Assert.Equal(new[] { "job-1", "job-2" }, await map.JobsForAsync(Clinician, Meeting));
    }

    [Fact]
    public async Task OneCliniciansMap_IsIsolatedFromAnothers()
    {
        var map = new InMemoryMeetingJobMap();
        await map.RecordAsync(Clinician, Meeting, "job-1");

        Assert.Empty(await map.JobsForAsync("clinician-2", Meeting));
    }

    [Fact]
    public async Task AnUnknownMeeting_IsEmpty_NotAnError()
    {
        var map = new InMemoryMeetingJobMap();

        Assert.Empty(await map.JobsForAsync(Clinician, "never-seen"));
    }
}
