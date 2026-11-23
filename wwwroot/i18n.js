const translations = {
    en: {
        subtitle: "Paste a Spotify track, album, or playlist link",
        howToTitle: "How to use SpotGet",
        howToStep1Title: "Copy a Spotify link",
        howToStep1Text: "In Spotify, open a track, album, or playlist, then choose Share → Copy link.",
        howToStep2Title: "Paste it into SpotGet",
        howToStep2Text: "Paste the link into the field above and select Get Info.",
        howToStep3Title: "Choose what to download",
        howToStep3Text: "Download a track as MP3, or download an album or playlist as a ZIP archive.",
        howToStep4Title: "Enjoy!",
        howToStep4Text: "Enjoy your music. ^^",
        independentTitle: "An independent project",
        independentText: "SpotGet is not affiliated with, endorsed by, or sponsored by Spotify. Spotify and its brand assets belong to their respective owners.",
        audioSourceText: "Spotify links are used to retrieve metadata. For MP3 downloads, SpotGet searches separately for an audio match; availability and accuracy may vary.",
        rightsNote: "Only download content you are authorized to use, and follow the terms that apply in your region.",
        placeholder: "Spotify track, album, or playlist link",
        pasteTitle: "Paste from clipboard",
        paste: "Paste",
        getTrackInfo: "Get Info",
        downloadMp3: "Download MP3",
        downloadPreparing: "Preparing your download…",
        downloadTrack: "Download this track",
        downloadCollection: "Download collection as ZIP",
        collectionPreparing: "Preparing playlist…",
        collectionProgress: "Downloading {progress} of {total} tracks",
        collectionPacking: "Creating ZIP archive…",
        collectionSending: "Sending ZIP to your browser…",
        downloadStarted: "Download started: {filename}",
        partialDownload: "ZIP downloaded, but {count} tracks were skipped. See _download-report.txt inside the archive.",
        album: "Album",
        playlist: "Playlist",
        tracks: "tracks",
        openInSpotify: "Open in Spotify",
        artistProfile: "Artist Profile",
        playlistCreator: "Playlist creator",
        playlistCreators: "Playlist creators",
        downloadCover: "Download cover image",
        footer: "SpotGet &copy; 2026 &mdash; made with ❤️",
        errConn: "Could not connect to the server.",
        errGeneric: "Something went wrong. Please try again.",
        errInvalidUrl: "Enter a valid Spotify link.",
        errInvalidTrackLink: "This does not look like a valid Spotify track link.",
        errDl: "Failed to download the track.",
        errDlGeneric: "An error occurred during download.",
        errCollection: "Could not process this album or playlist. Please try again.",
        errCollectionUnavailable: "This album or playlist could not be found or is unavailable.",
        errNoTracks: "No available tracks were found in this album or playlist.",
        errTooManyTracks: "A collection can contain up to 1,000 tracks per download.",
        errQueueBusy: "The download queue is busy. Please try again shortly.",
        errSpotify: "Could not retrieve Spotify information. Please try again later.",
        errCover: "Could not download the cover image.",
        errCoverInvalid: "The cover image link or file name is invalid.",
        errCoverDomain: "Only cover images hosted on Spotify's image CDN can be downloaded.",
        errCoverRedirect: "The cover image could not be retrieved from Spotify's image CDN.",
        errCoverUnsupported: "This cover image format is not supported.",
        errEmptyUrl: "The 'url' field cannot be empty.",
        errNotFound: "Track not found on Spotify."
    },
    uk: {
        subtitle: "Вставте посилання на трек, альбом або плейліст Spotify",
        howToTitle: "Як користуватися SpotGet",
        howToStep1Title: "Скопіюйте посилання Spotify",
        howToStep1Text: "У Spotify відкрийте трек, альбом або плейліст і виберіть «Поділитися» → «Копіювати посилання».",
        howToStep2Title: "Вставте посилання у SpotGet",
        howToStep2Text: "Вставте його в поле вище та натисніть «Отримати інфо».",
        howToStep3Title: "Виберіть, що завантажити",
        howToStep3Text: "Завантажте трек у форматі MP3 або альбом чи плейліст ZIP-архівом.",
        howToStep4Title: "Насолоджуйтеся!",
        howToStep4Text: "Приємного прослуховування! ^^",
        independentTitle: "Незалежний проєкт",
        independentText: "SpotGet не пов’язаний зі Spotify, не схвалений і не фінансується компанією Spotify. Spotify та її брендові матеріали належать відповідним власникам.",
        audioSourceText: "Посилання Spotify використовуються для отримання метаданих. Для MP3 SpotGet окремо шукає відповідний аудіозапис; доступність і точність збігу можуть відрізнятися.",
        rightsNote: "Завантажуйте лише контент, який маєте право використовувати, та дотримуйтеся чинних у вашому регіоні умов.",
        placeholder: "Посилання на трек, альбом або плейліст Spotify",
        pasteTitle: "Вставити з буфера обміну",
        paste: "Вставити",
        getTrackInfo: "Отримати інфо",
        downloadMp3: "Завантажити MP3",
        downloadPreparing: "Готуємо завантаження…",
        downloadTrack: "Завантажити цей трек",
        downloadCollection: "Завантажити колекцію ZIP-архівом",
        collectionPreparing: "Готуємо плейліст…",
        collectionProgress: "Завантажено {progress} з {total} треків",
        collectionPacking: "Створюємо ZIP-архів…",
        collectionSending: "Передаємо ZIP у браузер…",
        downloadStarted: "Завантаження розпочато: {filename}",
        partialDownload: "ZIP завантажено, але пропущено треків: {count}. Деталі — у _download-report.txt в архіві.",
        album: "Альбом",
        playlist: "Плейліст",
        tracks: "треків",
        openInSpotify: "Відкрити у Spotify",
        artistProfile: "Профіль виконавця",
        playlistCreator: "Автор плейліста",
        playlistCreators: "Автори плейліста",
        downloadCover: "Завантажити обкладинку",
        footer: "SpotGet &copy; 2026 &mdash; зроблено з ❤️",
        errConn: "Не вдалося підключитися до сервера.",
        errGeneric: "Сталася помилка. Спробуйте ще раз.",
        errInvalidUrl: "Введіть коректне посилання Spotify.",
        errInvalidTrackLink: "Це не схоже на коректне посилання Spotify на трек.",
        errDl: "Не вдалося завантажити трек.",
        errDlGeneric: "Під час завантаження сталася помилка.",
        errCollection: "Не вдалося обробити альбом або плейліст. Спробуйте ще раз.",
        errCollectionUnavailable: "Альбом або плейліст не знайдений чи недоступний.",
        errNoTracks: "У цьому альбомі або плейлісті не знайдено доступних треків.",
        errTooManyTracks: "За одне завантаження можна обробити не більше 1000 треків.",
        errQueueBusy: "Черга завантажень зайнята. Спробуйте трохи пізніше.",
        errSpotify: "Не вдалося отримати дані Spotify. Спробуйте пізніше.",
        errCover: "Не вдалося завантажити обкладинку.",
        errCoverInvalid: "Посилання на обкладинку або назва файлу некоректні.",
        errCoverDomain: "Можна завантажувати лише обкладинки з CDN зображень Spotify.",
        errCoverRedirect: "Не вдалося отримати обкладинку з CDN зображень Spotify.",
        errCoverUnsupported: "Цей формат обкладинки не підтримується.",
        errEmptyUrl: "Поле 'url' не може бути порожнім.",
        errNotFound: "Трек не знайдений на Spotify."
    }
};

let currentLang = localStorage.getItem('app_lang') || 'en';

function setLanguage(lang) {
    if (!translations[lang]) return;
    currentLang = lang;
    localStorage.setItem('app_lang', lang);
    document.documentElement.lang = lang;

    document.querySelectorAll('[data-i18n]').forEach(el => {
        const key = el.getAttribute('data-i18n');
        if (translations[lang][key]) el.innerHTML = translations[lang][key];
    });

    document.querySelectorAll('[data-i18n-placeholder]').forEach(el => {
        const key = el.getAttribute('data-i18n-placeholder');
        if (translations[lang][key]) el.placeholder = translations[lang][key];
    });

    document.querySelectorAll('[data-i18n-title]').forEach(el => {
        const key = el.getAttribute('data-i18n-title');
        if (translations[lang][key]) el.title = translations[lang][key];
    });

    document.querySelectorAll('.lang-pill').forEach(btn => {
        if (btn.dataset.lang === lang) {
            btn.classList.add('active');
        } else {
            btn.classList.remove('active');
        }
    });

    if (typeof refreshDynamicTranslations === 'function') {
        refreshDynamicTranslations();
    }
}

function translateError(msg, fallback = translations[currentLang].errGeneric) {
    return translateErrorMessage(msg, fallback);
}

function translateErrorMessage(msg, fallback) {
    const t = translations[currentLang];
    if (!msg) return fallback;

    const knownMessages = [
        t.errConn, t.errGeneric, t.errInvalidUrl, t.errInvalidTrackLink, t.errDl,
        t.errDlGeneric, t.errCollection, t.errCollectionUnavailable, t.errNoTracks,
        t.errTooManyTracks, t.errQueueBusy, t.errSpotify, t.errCover, t.errCoverInvalid,
        t.errCoverDomain, t.errCoverRedirect, t.errCoverUnsupported, t.errEmptyUrl,
        t.errNotFound
    ];
    if (knownMessages.includes(msg)) return msg;

    const message = String(msg).trim().toLocaleLowerCase();
    if (message.includes("вставте коректне посилання") || message.includes("вставте посилання spotify") ||
        message.includes("не може бути порожнім") ||
        (message.includes("url") && ["порожнім", "довгим", "empty", "long"].some(word => message.includes(word))) ||
        (message.includes("посилання") && message.includes("порожнім"))) return t.errInvalidUrl;

    if (message.includes("не вдалося розпізнати spotify track id") || message.includes("spotify track id"))
        return t.errInvalidTrackLink;
    if ((message.includes("трек") || message.includes("track")) &&
        (message.includes("не знайден") || message.includes("not found"))) return t.errNotFound;

    if ((message.includes("альбом") || message.includes("плейліст") || message.includes("collection") || message.includes("playlist")) &&
        (message.includes("не знайден") || message.includes("недоступ") || message.includes("not found") || message.includes("unavailable")))
        return t.errCollectionUnavailable;
    if (message.includes("не знайдено доступних треків") || message.includes("не вдалося завантажити жодного треку"))
        return t.errNoTracks;
    if (message.includes("не більше 1000") || message.includes("up to 1,000")) return t.errTooManyTracks;
    if ((message.includes("черг") && message.includes("завантаж")) || message.includes("queue")) return t.errQueueBusy;

    if (message.includes("назва обкладинки") || message.includes("посилання або назва обкладинки")) return t.errCoverInvalid;
    if (message.includes("лише зображення з cdn") || message.includes("only images from spotify cdn") || message.includes("за межі cdn"))
        return t.errCoverDomain;
    if ((message.includes("cdn") && message.includes("перенаправ")) ||
        (message.includes("обкладин") && (message.includes("перенаправ") || message.includes("отримати"))))
        return t.errCoverRedirect;
    if (message.includes("непідтримуваний формат обкладинки") || message.includes("unsupported cover image format"))
        return t.errCoverUnsupported;

    if (message.includes("spotify") && (message.includes("помил") || message.includes("не вдалося") || message.includes("не повернув")))
        return t.errSpotify;
    return fallback;
}

document.addEventListener('DOMContentLoaded', () => {
    setLanguage(currentLang);

    document.querySelectorAll('.lang-pill').forEach(btn => {
        btn.addEventListener('click', (e) => {
            setLanguage(e.target.dataset.lang);
        });
    });
});
