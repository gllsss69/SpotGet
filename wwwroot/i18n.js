const translations = {
    en: {
        subtitle: "Paste a Spotify track link and get all the details",
        placeholder: "https://open.spotify.com/track/...",
        pasteTitle: "Paste from clipboard",
        paste: "Paste",
        getTrackInfo: "Get Track Info",
        downloadMp3: "Download MP3",
        openInSpotify: "Open in Spotify",
        artistProfile: "Artist Profile",
        footer: "SpotGet &copy; 2026 &mdash; made with ❤️",
        errConn: "Could not connect to the server.",
        errDl: "Failed to download the track.",
        errDlGeneric: "An error occurred during download.",
        errEmptyUrl: "The 'url' field cannot be empty.",
        errNotFound: "Track not found on Spotify."
    },
    uk: {
        subtitle: "Вставте посилання на трек Spotify та отримайте всі деталі",
        placeholder: "https://open.spotify.com/track/...",
        pasteTitle: "Вставити з буфера обміну",
        paste: "Вставити",
        getTrackInfo: "Отримати інфо",
        downloadMp3: "Завантажити MP3",
        openInSpotify: "Відкрити у Spotify",
        artistProfile: "Профіль виконавця",
        footer: "SpotGet &copy; 2026 &mdash; зроблено з ❤️",
        errConn: "Не вдалося підключитися до сервера.",
        errDl: "Не вдалося завантажити трек.",
        errDlGeneric: "Під час завантаження сталася помилка.",
        errEmptyUrl: "Поле 'url' не може бути порожнім.",
        errNotFound: "Трек не знайдений на Spotify."
    }
};

let currentLang = localStorage.getItem('app_lang') || 'en';

function setLanguage(lang) {
    if (!translations[lang]) return;
    currentLang = lang;
    localStorage.setItem('app_lang', lang);
    
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
}

function translateError(msg) {
    if (!msg) return translations[currentLang].errConn;
    if (msg.includes("не може бути порожнім")) return translations[currentLang].errEmptyUrl;
    if (msg.includes("не знайдений на Spotify") || msg.includes("Not Found")) return translations[currentLang].errNotFound;
    return msg; 
}

document.addEventListener('DOMContentLoaded', () => {
    setLanguage(currentLang);
    
    document.querySelectorAll('.lang-pill').forEach(btn => {
        btn.addEventListener('click', (e) => {
            setLanguage(e.target.dataset.lang);
        });
    });
});
