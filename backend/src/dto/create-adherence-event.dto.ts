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
}

export class CreateAdherenceEventDto {
  @IsEnum(EventType)
  event_type: EventType;

  // Support both 'timestamp' and 'event_timestamp' for API compatibility
  // At least one must be provided
  @IsISO8601()
  @ValidateIf((o) => !o.timestamp)
  event_timestamp?: string;

  @IsISO8601()
  @ValidateIf((o) => !o.event_timestamp)
  timestamp?: string;

  @IsString()
  @IsNotEmpty({ message: 'nt field is required (Windows NT account sam_account_name)' })
  @MaxLength(100)
  @Matches(/^[^\\]+$/, {
    message: 'nt must be sam_account_name only (e.g., z.salah.3613), no domain prefix',
  })
  nt: string; // Windows NT account (sam_account_name only, e.g., z.salah.3613)

  @IsString()
  @IsOptional()
  application_name?: string;

  @IsString()
  @MaxLength(500)
  @IsOptional()
  application_path?: string;

  @IsString()
  @MaxLength(500)
  @IsOptional()
  window_title?: string;

  @IsBoolean()
  @IsOptional()
  is_work_application?: boolean;

  @IsObject()
  @IsOptional()
  metadata?: Record<string, any>;
}

export class BatchEventsDto {
  @IsArray()
  @ValidateNested({ each: true })
  @Type(() => CreateAdherenceEventDto)
  events: CreateAdherenceEventDto[];
}

