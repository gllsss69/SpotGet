// SpotGet Frontend

const form = document.getElementById('trackForm');
const urlInput = document.getElementById('spotifyUrl');
const pasteBtn = document.getElementById('pasteBtn');
const submitBtn = document.getElementById('submitBtn');
const btnText = submitBtn.querySelector('.btn-text');
const btnLoader = submitBtn.querySelector('.btn-loader');

const errorSection = document.getElementById('errorSection');
const errorMessage = document.getElementById('errorMessage');

const resultSection = document.getElementById('resultSection');
const coverImg = document.getElementById('coverImg');
const trackTitle = document.getElementById('trackTitle');
const trackArtist = document.getElementById('trackArtist');
const trackAlbum = document.getElementById('trackAlbum');
const trackDuration = document.getElementById('trackDuration');
const trackLink = document.getElementById('trackLink');

const downloadBtn = document.getElementById('downloadBtn');
const dlText = downloadBtn.querySelector('.dl-text');
const dlLoader = downloadBtn.querySelector('.dl-loader');

const resultCover = document.querySelector('.result-cover');
const coverOverlay = document.querySelector('.cover-overlay');
const previewProgress = document.getElementById('previewProgress');

const artistSection     = document.getElementById('artistSection');
const artistAvatar      = document.getElementById('artistAvatar');
const artistName        = document.getElementById('artistName');
const artistDescription = document.getElementById('artistDescription');
const artistLink        = document.getElementById('artistLink');

let currentAudio = null;
let isPlaying = false;

// Paste button
pasteBtn.addEventListener('click', async () => {
    try {
        const text = await navigator.clipboard.readText();
        urlInput.value = text;
        urlInput.focus();
    } catch {
        // Clipboard API may be blocked — ignore silently
    }
});

// Form submit
form.addEventListener('submit', async (e) => {
    e.preventDefault();

    const url = urlInput.value.trim();
    if (!url) return;

    setLoading(true);
    hideError();
    hideResult();
    stopAudio();

    try {
        const response = await fetch('/api/track-info', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ url }),
        });

        const data = await response.json();

        if (!response.ok) {
            showError(translateError(data.error || data.detail || 'Unknown error'));
            return;
        }

        showResult(data);
    } catch (err) {
        showError(translations[currentLang].errConn);
    } finally {
        setLoading(false);
    }
});

// Download button
downloadBtn.addEventListener('click', async () => {
    const url = urlInput.value.trim();
    if (!url) return;

    setDownloadLoading(true);
    hideError();

    try {
        const response = await fetch('/api/download', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ url }),
        });

        if (!response.ok) {
            const data = await response.json().catch(() => ({}));
            showError(translateError(data.error || data.detail || translations[currentLang].errDl));
            return;
        }

        const blob = await response.blob();

        let filename = `${trackArtist.textContent} - ${trackTitle.textContent}.mp3`;
        const disposition = response.headers.get('Content-Disposition');
        if (disposition && disposition.indexOf('filename=') !== -1) {
            const match = disposition.match(/filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/);
            if (match && match[1]) {
                filename = decodeURIComponent(match[1].replace(/['"]/g, ''));
            }
        }

        const downloadUrl = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.style.display = 'none';
        a.href = downloadUrl;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        window.URL.revokeObjectURL(downloadUrl);
        a.remove();

    } catch (err) {
        showError(translations[currentLang].errDlGeneric);
    } finally {
        setDownloadLoading(false);
    }
});

// Preview Player

coverOverlay.addEventListener('click', () => {
    if (!currentAudio) return;

    if (isPlaying) {
        currentAudio.pause();
        isPlaying = false;
    } else {
        currentAudio.play();
        isPlaying = true;
    }
    updatePlayIcon(isPlaying);
});

function updatePlayIcon(playing) {
    if (playing) {
        resultCover.classList.add('playing');
        coverOverlay.innerHTML = `<svg width="48" height="48" viewBox="0 0 24 24" fill="white">
            <rect x="6" y="4" width="4" height="16"/>
            <rect x="14" y="4" width="4" height="16"/>
        </svg>`;
    } else {
        resultCover.classList.remove('playing');
        coverOverlay.innerHTML = `<svg width="48" height="48" viewBox="0 0 24 24" fill="white">
            <polygon points="5,3 19,12 5,21"/>
        </svg>`;
    }
}

function stopAudio() {
    if (currentAudio) {
        currentAudio.pause();
        currentAudio.currentTime = 0;
        currentAudio = null;
    }
    isPlaying = false;
    previewProgress.style.width = '0%';
    updatePlayIcon(false);
}

// Helpers

function setDownloadLoading(on) {
    downloadBtn.disabled = on;
    dlText.hidden = on;
    dlLoader.hidden = !on;
}

function setLoading(on) {
    submitBtn.disabled = on;
    btnText.hidden = on;
    btnLoader.hidden = !on;
}

function showError(message) {
    errorMessage.textContent = message;
    errorSection.hidden = false;
}

function hideError() {
    errorSection.hidden = true;
}

function showResult(track) {
    coverImg.src = track.coverUrl || '';
    coverImg.alt = `${track.album} — cover`;
    trackTitle.textContent = track.title;
    trackArtist.textContent = track.artist;
    trackAlbum.textContent = `${track.album}`;
    trackDuration.textContent = `⏱ ${formatDuration(track.durationMs)}`;
    trackLink.href = track.spotifyUrl;

    if (track.previewUrl) {
        currentAudio = new Audio(track.previewUrl);
        currentAudio.onended = () => stopAudio();
        currentAudio.ontimeupdate = () => {
            if (currentAudio && currentAudio.duration) {
                const percent = (currentAudio.currentTime / currentAudio.duration) * 100;
                previewProgress.style.width = `${percent}%`;
            }
        };
        coverOverlay.hidden = false;
    } else {
        coverOverlay.hidden = true;
    }

    resultSection.hidden = false;

    // Artist card
    if (track.artistInfo) {
        artistAvatar.src = track.artistInfo.avatarUrl || '';
        artistAvatar.alt = `${track.artistInfo.name} — avatar`;
        artistName.textContent = track.artistInfo.name;
        artistDescription.textContent = track.artistInfo.description || '';
        artistLink.href = track.artistInfo.spotifyUrl || '#';
        artistSection.hidden = false;
    } else {
        artistSection.hidden = true;
    }
}

function hideResult() {
    resultSection.hidden = true;
    artistSection.hidden = true;
}

function formatDuration(ms) {
    const totalSeconds = Math.floor(ms / 1000);
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return `${minutes}:${seconds.toString().padStart(2, '0')}`;
}

// Visitor counter
(async () => {
    try {
        const res = await fetch('/api/visitors');
        const data = await res.json();
        const el = document.getElementById('visitorCount');
        if (el && data.count !== undefined) {
            el.textContent = data.count.toLocaleString();
            el.classList.add('animate');
        }
    } catch {
        // Silently ignore — badge will stay with "—"
    }
})();
