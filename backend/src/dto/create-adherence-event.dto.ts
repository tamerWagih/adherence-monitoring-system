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
  CALL_START = 'CALL_START',
  CALL_END = 'CALL_END',
}

export class CreateAdherenceEventDto {
  @IsEnum(EventType)
  event_type: EventType;

  @IsISO8601()
  event_timestamp: string;

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

