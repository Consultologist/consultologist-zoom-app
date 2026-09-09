// The Zoom panel. It resolves the meeting from the Zoom Apps SDK and drives the
// two legs through this app's own backend: list the meeting's consults (Leg 1)
// and generate one from the meeting transcript (Leg 2). No engine calls, no
// tokens here — the backend holds the clinician's Entra and Zoom tokens.
'use strict';

const $ = (id) => document.getElementById(id);
const setStatus = (msg) => { $('status').textContent = msg; };

let meetingUuid = null;

async function api(path, options) {
  const res = await fetch(path, { credentials: 'same-origin', ...options });
  if (res.status === 401) {
    // Not signed into Microsoft Entra yet — send them through the app's sign-in.
    location.href = '/MicrosoftIdentity/Account/SignIn';
    throw new Error('signing in');
  }
  return res;
}

async function loadConsults() {
  if (!meetingUuid) return;
  const res = await api(`/api/meeting/${encodeURIComponent(meetingUuid)}/consults`);
  if (!res.ok) return;
  const jobs = await res.json();
  const ul = $('consults');
  ul.innerHTML = '';
  if (!jobs.length) { ul.innerHTML = '<li class="muted">None yet.</li>'; return; }
  for (const j of jobs) {
    const li = document.createElement('li');
    li.textContent = `${j.status} · ${j.jobId.slice(0, 8)}… · ${new Date(j.createdAtUtc).toLocaleString()}`;
    ul.appendChild(li);
  }
}

async function pollJob(jobId) {
  for (;;) {
    const res = await api(`/api/jobs/${encodeURIComponent(jobId)}`);
    const job = await res.json();
    if (['Completed', 'Failed', 'Cancelled'].includes(job.status)) {
      setStatus(job.status === 'Completed'
        ? `Done. Effective-input hash: ${job.effectiveInputHash ?? '—'}`
        : `Ended: ${job.status}`);
      await loadConsults();
      return;
    }
    setStatus(`Working… (${job.status})`);
    await new Promise((r) => setTimeout(r, 3000));
  }
}

async function generate() {
  if (!meetingUuid) return;
  $('generate').disabled = true;
  setStatus('Fetching the transcript and starting the consult…');
  try {
    const res = await api(`/api/meeting/${encodeURIComponent(meetingUuid)}/generate`, { method: 'POST' });
    if (res.status === 409) { setStatus('Connect your Zoom account first.'); return; }
    if (res.status === 404) { setStatus('No transcript found for this meeting yet.'); return; }
    if (!res.ok) { setStatus(`Could not start: ${res.status}`); return; }
    const { jobId } = await res.json();
    await pollJob(jobId);
  } finally {
    $('generate').disabled = false;
  }
}

async function main() {
  if (typeof zoomSdk === 'undefined') {
    $('meeting').textContent = 'Open this inside Zoom to use it.';
    return;
  }
  try {
    await zoomSdk.config({
      capabilities: ['getRunningContext', 'getMeetingContext', 'getMeetingUUID', 'getUserContext'],
    });
    const ctx = await zoomSdk.getRunningContext();
    if (ctx.context === 'inMeeting') {
      const { meetingUUID } = await zoomSdk.getMeetingUUID();
      meetingUuid = meetingUUID;
      $('meeting').textContent = 'In meeting.';
      $('generate').disabled = false;
      await loadConsults();
    } else {
      $('meeting').textContent = 'Open the app during a meeting to use its transcript.';
    }
  } catch (e) {
    $('meeting').textContent = `Zoom context unavailable: ${e.message}`;
  }
}

$('generate').addEventListener('click', generate);
main();
