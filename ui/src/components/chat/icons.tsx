import type { SVGProps } from 'react';

type IconProps = SVGProps<SVGSVGElement> & { size?: number };

function Svg({ size = 14, children, ...rest }: IconProps & { children: React.ReactNode }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.75}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      {...rest}
    >
      {children}
    </svg>
  );
}

export const ArrowUpRightIcon = (p: IconProps) => (
  <Svg {...p}><path d="M7 17 17 7M9 7h8v8" /></Svg>
);

export const PlayIcon = (p: IconProps) => (
  <Svg {...p}><path d="M7 5v14l12-7z" fill="currentColor" stroke="none" /></Svg>
);

export const PlusIcon = (p: IconProps) => (
  <Svg {...p}><path d="M12 5v14M5 12h14" /></Svg>
);

export const RecordIcon = (p: IconProps) => (
  <Svg {...p}><circle cx="12" cy="12" r="6" fill="currentColor" stroke="none" /></Svg>
);

export const ClipboardListIcon = (p: IconProps) => (
  <Svg {...p}>
    <rect x="6" y="4" width="12" height="16" rx="2" />
    <path d="M9 4h6v3H9zM9 11h6M9 14h6M9 17h4" />
  </Svg>
);

export const TableIcon = (p: IconProps) => (
  <Svg {...p}>
    <rect x="4" y="5" width="16" height="14" rx="2" />
    <path d="M4 10h16M10 5v14" />
  </Svg>
);

export const CopyIcon = (p: IconProps) => (
  <Svg {...p}>
    <rect x="9" y="9" width="11" height="11" rx="2" />
    <path d="M5 15V6a2 2 0 0 1 2-2h9" />
  </Svg>
);

export const CheckIcon = (p: IconProps) => (
  <Svg {...p}><path d="m5 12 4 4 10-10" /></Svg>
);

export const CloseIcon = (p: IconProps) => (
  <Svg {...p}><path d="M6 6l12 12M18 6 6 18" /></Svg>
);

export const ChevronDownIcon = (p: IconProps) => (
  <Svg {...p}><path d="m6 9 6 6 6-6" /></Svg>
);

export const SearchIcon = (p: IconProps) => (
  <Svg {...p}>
    <circle cx="11" cy="11" r="6" />
    <path d="m20 20-3.5-3.5" />
  </Svg>
);

export const ExpandIcon = (p: IconProps) => (
  <Svg {...p}>
    <path d="M9 3H3v6M15 21h6v-6M21 3l-7 7M3 21l7-7" />
  </Svg>
);

export const SidebarWideIcon = (p: IconProps) => (
  <Svg {...p}>
    <rect x="3" y="4" width="18" height="16" rx="2" />
    <path d="M9 4v16" />
  </Svg>
);

export const CodeIcon = (p: IconProps) => (
  <Svg {...p}><path d="m8 7-5 5 5 5M16 7l5 5-5 5" /></Svg>
);

export const SqlIcon = (p: IconProps) => (
  <Svg {...p}>
    <ellipse cx="12" cy="6" rx="8" ry="3" />
    <path d="M4 6v6c0 1.7 3.6 3 8 3s8-1.3 8-3V6M4 12v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6" />
  </Svg>
);

export const TrashIcon = (p: IconProps) => (
  <Svg {...p}>
    <path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2M6 7l1 13a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-13" />
  </Svg>
);

export const RefreshIcon = (p: IconProps) => (
  <Svg {...p}>
    <path d="M21 12a9 9 0 1 1-3-6.7M21 4v5h-5" />
  </Svg>
);
