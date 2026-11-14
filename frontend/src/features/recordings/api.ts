import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@/shared/api/client";
import type {
  ApplyRecordingResultDto,
  RecordingDto,
  RecordingEpisodesDto,
  RecordingProposalDto,
} from "@/shared/api/types";

export interface StartRecordingInput {
  label?: string;
  /** What the user is about to demonstrate (optional). */
  intent?: string;
  allowedOrigins?: string[];
  captureBodies?: boolean;
  maskAllInputs?: boolean;
}

export interface ApplyRecordingInput {
  acceptedSelectors: string[];
  enrichExisting: boolean;
  applyFlows: boolean;
  /** Replace elements learned from earlier recordings (needs a "fresh" proposal). */
  replaceLearned?: boolean;
}

/**
 * "Learn by watching": MAZDA attaches a recorder to the user's tab, captures
 * input, DOM mutations, network and navigation, then an LLM turns the
 * condensed episodes into elements + flows the user reviews and applies.
 */
export const recordingsApi = {
  list: (sigId: string) => api.get<RecordingDto[]>(`/api/signatures/${sigId}/recordings`),
  start: (sigId: string, body: StartRecordingInput) =>
    api.post<RecordingDto>(`/api/signatures/${sigId}/recordings`, body),
  get: (id: string) => api.get<RecordingDto>(`/api/recordings/${id}`),
  stop: (id: string) => api.post<RecordingDto>(`/api/recordings/${id}/stop`, {}),
  episodes: (id: string) => api.get<RecordingEpisodesDto>(`/api/recordings/${id}/episodes`),
  analyze: (id: string) => api.post<RecordingProposalDto>(`/api/recordings/${id}/analyze`, {}),
  /** All (or the given) completed recordings of a signature, analyzed together. */
  analyzeAll: (sigId: string, body: { recordingIds?: string[]; fresh?: boolean }) =>
    api.post<RecordingProposalDto>(`/api/signatures/${sigId}/recordings/analyze`, body),
  apply: (id: string, body: ApplyRecordingInput) =>
    api.post<ApplyRecordingResultDto>(`/api/recordings/${id}/apply`, body),
  update: (id: string, body: { label?: string; useForGrounding?: boolean; intent?: string }) =>
    api.patch<RecordingDto>(`/api/recordings/${id}`, body),
  remove: (id: string) => api.del<void>(`/api/recordings/${id}`),
};

const listKey = (sigId: string | undefined) => ["recordings", sigId];

export function useRecordings(sigId: string | undefined) {
  return useQuery({
    queryKey: listKey(sigId),
    queryFn: () => recordingsApi.list(sigId!),
    enabled: !!sigId,
  });
}

export function useRecordingEpisodes(id: string | null) {
  return useQuery({
    queryKey: ["recording-episodes", id],
    queryFn: () => recordingsApi.episodes(id!),
    enabled: !!id,
  });
}

function useInvalidateList(sigId: string | undefined) {
  const qc = useQueryClient();
  return () => qc.invalidateQueries({ queryKey: listKey(sigId) });
}

export function useStartRecording(sigId: string | undefined) {
  const invalidate = useInvalidateList(sigId);
  return useMutation({
    mutationFn: (body: StartRecordingInput) => recordingsApi.start(sigId!, body),
    onSuccess: invalidate,
  });
}

export function useStopRecording(sigId: string | undefined) {
  const invalidate = useInvalidateList(sigId);
  return useMutation({ mutationFn: recordingsApi.stop, onSuccess: invalidate });
}

export function useAnalyzeRecording(sigId: string | undefined) {
  const invalidate = useInvalidateList(sigId);
  return useMutation({ mutationFn: recordingsApi.analyze, onSuccess: invalidate });
}

export function useAnalyzeAllRecordings(sigId: string | undefined) {
  const invalidate = useInvalidateList(sigId);
  return useMutation({
    mutationFn: (body: { recordingIds?: string[]; fresh?: boolean }) => recordingsApi.analyzeAll(sigId!, body),
    onSuccess: invalidate,
  });
}

export function useApplyRecording(sigId: string | undefined) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, body }: { id: string; body: ApplyRecordingInput }) => recordingsApi.apply(id, body),
    onSuccess: (data) => {
      qc.invalidateQueries({ queryKey: listKey(sigId) });
      qc.invalidateQueries({ queryKey: ["signatures"] });
      qc.setQueryData(["signature", data.signature.id], data.signature);
    },
  });
}

export function useUpdateRecording(sigId: string | undefined) {
  const invalidate = useInvalidateList(sigId);
  return useMutation({
    mutationFn: ({ id, body }: { id: string; body: { label?: string; useForGrounding?: boolean; intent?: string } }) =>
      recordingsApi.update(id, body),
    onSuccess: invalidate,
  });
}

export function useDeleteRecording(sigId: string | undefined) {
  const invalidate = useInvalidateList(sigId);
  return useMutation({ mutationFn: recordingsApi.remove, onSuccess: invalidate });
}
