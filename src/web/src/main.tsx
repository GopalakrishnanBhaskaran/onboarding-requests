import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';

const style = document.createElement('style');
style.textContent = 'html,body{margin:0;padding:0;}';
document.head.appendChild(style);

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
