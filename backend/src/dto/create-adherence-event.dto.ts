import {
  IsEnum,
  IsISO8601,
  IsString,
  IsOptional,
  IsBoolean,
  IsObject,
  MaxLength,
  ValidateNested,
  IsArray,
  ValidateIf,
  IsNotEmpty,
  Matches,
} from 'class-validator';
import { Type } from 'class-transformer';
import { ApiProperty, ApiPropertyOptional } from '@nestjs/swagger';

export enum EventType {
  LOGIN = 'LOGIN',
  LOGOFF = 'LOGOFF',
  IDLE_START = 'IDLE_START',
  IDLE_END = 'IDLE_END',
  BREAK_START = 'BREAK_START',
  BREAK_END = 'BREAK_END',
  WINDOW_CHANGE = 'WINDOW_CHANGE',
  APPLICATION_START = 'APPLICATION_START',
  APPLICATION_END = 'APPLICATION_END',
  APPLICATION_FOCUS = 'APPLICATION_FOCUS', // Application window gained focus
  CALL_START = 'CALL_START',
  CALL_END = 'CALL_END',
  // Day 8: Advanced Activity Detection
  TEAMS_MEETING_START = 'TEAMS_MEETING_START',
  TEAMS_MEETING_END = 'TEAMS_MEETING_END',
  TEAMS_CHAT_ACTIVE = 'TEAMS_CHAT_ACTIVE',
  BROWSER_TAB_CHANGE = 'BROWSER_TAB_CHANGE',
  // Day 9: Client Website & Calling App Detection
  CLIENT_WEBSITE_ACCESS = 'CLIENT_WEBSITE_ACCESS',
  CALLING_APP_START = 'CALLING_APP_START',
  CALLING_APP_END = 'CALLING_APP_END',
  CALLING_APP_IN_CALL = 'CALLING_APP_IN_CALL',
}

export class CreateAdherenceEventDto {
  @ApiProperty({
    enum: EventType,
    description: 'Type of adherence event',
    example: EventType.LOGIN,
  })
  @IsEnum(EventType)
  event_type: EventType;

  @ApiPropertyOptional({
    description: 'Event timestamp in ISO 8601 format (alternative to timestamp)',
    example: '2025-12-23T10:00:00Z',
  })
  @IsISO8601()
  @ValidateIf((o) => !o.timestamp)
  event_timestamp?: string;

  @ApiPropertyOptional({
    description: 'Event timestamp in ISO 8601 format (alternative to event_timestamp)',
    example: '2025-12-23T10:00:00Z',
  })
  @IsISO8601()
  @ValidateIf((o) => !o.event_timestamp)
  timestamp?: string;

  @ApiProperty({
    description: 'Windows NT account (sam_account_name only, e.g., z.salah.3613)',
    example: 'localadmin',
    maxLength: 100,
  })
  @IsString()
  @IsNotEmpty({ message: 'nt field is required (Windows NT account sam_account_name)' })
  @MaxLength(100)
  @Matches(/^[^\\]+$/, {
    message: 'nt must be sam_account_name only (e.g., z.salah.3613), no domain prefix',
  })
  nt: string;

  @ApiPropertyOptional({
    description: 'Application name',
    example: 'Microsoft Teams',
    maxLength: 255,
  })
  @IsString()
  @IsOptional()
  application_name?: string;

  @ApiPropertyOptional({
    description: 'Application file path',
    example: 'C:\\Program Files\\Microsoft\\Teams\\Teams.exe',
    maxLength: 500,
  })
  @IsString()
  @MaxLength(500)
  @IsOptional()
  application_path?: string;

  @ApiPropertyOptional({
    description: 'Window title',
    example: 'Teams - Chat',
    maxLength: 500,
  })
  @IsString()
  @MaxLength(500)
  @IsOptional()
  window_title?: string;

  @ApiPropertyOptional({
    description: 'Whether this is a work-related application',
    example: true,
  })
  @IsBoolean()
  @IsOptional()
  is_work_application?: boolean;

  @ApiPropertyOptional({
    description: 'Additional metadata as key-value pairs',
    example: { callDuration: 300, meetingId: 'meeting-123' },
  })
  @IsObject()
  @IsOptional()
  metadata?: Record<string, any>;
}

export class BatchEventsDto {
  @ApiProperty({
    description: 'Array of events to process in batch',
    type: [CreateAdherenceEventDto],
    example: [
      {
        event_type: EventType.LOGIN,
        timestamp: '2025-12-23T10:00:00Z',
        nt: 'localadmin',
      },
      {
        event_type: EventType.BREAK_START,
        timestamp: '2025-12-23T14:00:00Z',
        nt: 'localadmin',
      },
    ],
  })
  @IsArray()
  @ValidateNested({ each: true })
  @Type(() => CreateAdherenceEventDto)
  events: CreateAdherenceEventDto[];
}

