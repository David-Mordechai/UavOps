// Hebrew scenario set — REAL human-recorded speech from Google's FLEURS dataset (he_il test
// split), not synthesized. Replaces an earlier espeak-ng-based version: espeak's Hebrew phonetic
// module was confirmed this session to produce audio bad enough that even a completely unrelated,
// independently-good Hebrew STT model transcribed it as near-total nonsense (~98% WER, 0/10 exact
// matches) — a TTS-quality artifact, not a real STT measurement. These clips are genuine recorded
// human speech with real ground-truth transcripts, pulled via the `datasets` library
// (`google/fleurs`, `he_il` config, streaming) and tested once already via a direct script
// (avg WER 16.4%, closely matching ivrit.ai's own published 17.4% FLEURS/he benchmark for this
// same model — a real, credible number). This scenario set re-runs the same 10 clips through the
// actual browser harness so the run can be watched/heard directly, not just read as a number.
const HEBREW_SCENARIOS = [
  { id: "01", text: "מרטלי השביע אתמול ועדת בחירות זמנית חדשה בת תשעה חברים", audioFile: "audio-he-fleurs/00.wav" },
  { id: "02", text: "הנמל היה האתר של סכסוך ימי מפורסם מ-1889 כאשר שבע אוניות מגרמניה ארה\"ב ובריטניה סירבו לצאת את הנמל", audioFile: "audio-he-fleurs/01.wav" },
  { id: "03", text: "סולטאן מרוקו בנה את העיר מחדש בתור דאר אל-ביידא והיא קיבלה את השם קזבלנקה מסוחרים ספרדים שהקימו בה מרכזי סחר", audioFile: "audio-he-fleurs/02.wav" },
  { id: "04", text: "מועצת ההתעמלות של ארה\"ב והוועד האולימפי שותפים לאותה מטרה להפוך את ענף ההתעמלות וענפים אחרים לכמה שיותר בטוחים לספורטאים לשאוף להגשים את חלומותיהם בסביבה בטוחה חיובית ומועצמת", audioFile: "audio-he-fleurs/03.wav" },
  { id: "05", text: "נושאים אחרים העומדים על הפרק בבאלי כוללים הצלת היערות הנותרים בעולם ושיתוף טכנולוגיות שיעזרו למדינות מתפתחות לצמוח בדרכים פחות מזהמות", audioFile: "audio-he-fleurs/04.wav" },
  { id: "06", text: "בזמן תקופה זו בהיסטוריה האירופאית הכנסייה הקתולית שהפכה לעשירה ורבת כוח ספגה ביקורת קפדנית", audioFile: "audio-he-fleurs/05.wav" },
  { id: "07", text: "יתכן שיש יותר ימות ירחיות בצד הקרוב מכיוון שהקרום דק יותר כך היה קל יותר ללבה לעלות אל פני השטח", audioFile: "audio-he-fleurs/06.wav" },
  { id: "08", text: "השירות לעתים קרובות משמש כלי שיט כולל ספינות תענוגות כמו גם משלחות מחקר שצריכות תקשורת נתונים מרחוק ותקשורת קולית", audioFile: "audio-he-fleurs/07.wav" },
  { id: "09", text: "מרבית האיים הקטנים יותר הם מדינות עצמאיות או הקשורות לצרפת וידועים כאתרי נופש יוקרתיים בחוף", audioFile: "audio-he-fleurs/08.wav" },
  { id: "10", text: "סולטאן מרוקו בנה את העיר מחדש בתור דאר אל-ביידא והיא קיבלה את השם קזבלנקה מסוחרים ספרדים שהקימו בה מרכזי סחר", audioFile: "audio-he-fleurs/09.wav" },
];
