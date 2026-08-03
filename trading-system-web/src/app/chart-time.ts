import { Time } from 'lightweight-charts';

const istTimeFormatter = new Intl.DateTimeFormat('en-IN', {
  timeZone: 'Asia/Kolkata',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
});

const istCrosshairFormatter = new Intl.DateTimeFormat('en-IN', {
  timeZone: 'Asia/Kolkata',
  day: '2-digit',
  month: 'short',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
});

export function formatChartTimeIst(time: Time): string {
  return typeof time === 'number' ? istTimeFormatter.format(new Date(time * 1000)) : '';
}

export function formatCrosshairTimeIst(time: Time): string {
  return typeof time === 'number' ? `${istCrosshairFormatter.format(new Date(time * 1000))} IST` : '';
}
