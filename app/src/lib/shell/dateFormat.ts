// Every toLocaleTimeString call secretly builds a whole new locale formatter;
// doing that for hundreds of turns during one render froze the app.

const clockTimeFormatter = new Intl.DateTimeFormat(undefined, {
  hour: 'numeric',
  minute: '2-digit'
});
const fullDateTimeFormatter = new Intl.DateTimeFormat(undefined, {
  year: 'numeric',
  month: 'numeric',
  day: 'numeric',
  hour: 'numeric',
  minute: 'numeric',
  second: 'numeric'
});
const monthDayTimeFormatter = new Intl.DateTimeFormat(undefined, {
  month: 'short',
  day: 'numeric',
  hour: 'numeric',
  minute: '2-digit'
});
const monthDayYearFormatter = new Intl.DateTimeFormat(undefined, {
  month: 'short',
  day: 'numeric',
  year: 'numeric'
});
const monthDayFormatter = new Intl.DateTimeFormat(undefined, {
  month: 'short',
  day: 'numeric'
});
const fullTimeFormatter = new Intl.DateTimeFormat(undefined, {
  hour: 'numeric',
  minute: 'numeric',
  second: 'numeric'
});

const weekdayTimeFormatter = new Intl.DateTimeFormat(undefined, {
  weekday: 'long',
  hour: 'numeric',
  minute: '2-digit'
});

export const formatClockTime = (date: Date): string => clockTimeFormatter.format(date);
export const formatWeekdayTime = (date: Date): string => weekdayTimeFormatter.format(date);
export const formatFullDateTime = (date: Date): string => fullDateTimeFormatter.format(date);
export const formatMonthDayTime = (date: Date): string => monthDayTimeFormatter.format(date);
export const formatMonthDayYear = (date: Date): string => monthDayYearFormatter.format(date);
export const formatMonthDay = (date: Date): string => monthDayFormatter.format(date);
export const formatFullTime = (date: Date): string => fullTimeFormatter.format(date);
